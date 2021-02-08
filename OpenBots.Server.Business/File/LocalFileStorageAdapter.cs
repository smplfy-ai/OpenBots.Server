﻿using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using OpenBots.Server.Business.Interfaces;
using OpenBots.Server.DataAccess.Exceptions;
using OpenBots.Server.DataAccess.Repositories;
using OpenBots.Server.DataAccess.Repositories.Interfaces;
using OpenBots.Server.Model;
using OpenBots.Server.Model.Core;
using OpenBots.Server.Model.File;
using OpenBots.Server.ViewModel.File;
using OpenBots.Server.Web.Webhooks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using IOFile = System.IO.File;

namespace OpenBots.Server.Business.File
{
    public class LocalFileStorageAdapter : IFileStorageAdapter
    {
        private readonly IServerFileRepository _serverFileRepository;
        private readonly IFileAttributeRepository _fileAttributeRepository;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IOrganizationManager _organizationManager;
        private readonly IServerFolderRepository _serverFolderRepository;
        private readonly IServerDriveRepository _serverDriveRepository;
        private readonly IWebhookPublisher _webhookPublisher;
        private readonly IDirectoryManager _directoryManager;
        private readonly IAuditLogRepository _auditLogRepository;

        public IConfiguration Configuration { get; }

        public LocalFileStorageAdapter(
            IServerFileRepository serverFileRepository,
            IFileAttributeRepository fileAttributeRepository,
            IHttpContextAccessor httpContextAccessor,
            IOrganizationManager organizationManager,
            IServerFolderRepository serverFolderRepository,
            IServerDriveRepository serverDriveRepository,
            IConfiguration configuration,
            IWebhookPublisher webhookPublisher,
            IDirectoryManager directoryManager,
            IAuditLogRepository auditLogRepository)
        {
            _fileAttributeRepository = fileAttributeRepository;
            _serverFileRepository = serverFileRepository;
            _httpContextAccessor = httpContextAccessor;
            _organizationManager = organizationManager;
            _serverFolderRepository = serverFolderRepository;
            _serverDriveRepository = serverDriveRepository;
            _webhookPublisher = webhookPublisher;
            _directoryManager = directoryManager;
            _auditLogRepository = auditLogRepository;
            Configuration = configuration;
        }

        public PaginatedList<FileFolderViewModel> GetFilesFolders(bool? isFile = null, string driveName = null, Predicate<FileFolderViewModel> predicate = null, string sortColumn = "", OrderByDirectionType direction = OrderByDirectionType.Ascending, int skip = 0, int take = 100)
        {
            var filesFolders = new PaginatedList<FileFolderViewModel>();
            var files = new List<FileFolderViewModel>();
            Guid? driveId = GetDriveId(driveName);

            if (isFile.Equals(true))
            {
                //get all files
                filesFolders = _serverFileRepository.FindAllView(driveId, predicate, sortColumn, direction, skip, take);
            }
            else if (isFile.Equals(false))
            {
                //get all folders
                filesFolders = _serverFolderRepository.FindAllView(driveId, predicate, sortColumn, direction, skip, take);
            }
            else
            {
                //get all folders and files
                filesFolders = _serverFolderRepository.FindAllFilesFoldersView(driveId, predicate, sortColumn, direction, skip, take);
            }

            return filesFolders;
        }

        public List<FileFolderViewModel> AddFileFolder(FileFolderViewModel request, string driveName)
        {
            var fileFolderList = new List<FileFolderViewModel>();
            var newFileFolder = new FileFolderViewModel();

            ServerDrive drive = GetDriveByName(driveName);

            if ((bool)request.IsFile)
            {
                foreach (var file in request.Files)
                {
                    if (file == null)
                        throw new EntityOperationException("No file uploaded");

                    long size = file.Length;
                    if (size <= 0)
                        throw new EntityOperationException($"File size of file {file.FileName} cannot be 0");

                    //add file
                    request.FullStoragePath = Path.Combine(request.StoragePath, file.FileName);
                    newFileFolder = SaveFile(request, file, drive);
                    fileFolderList.Add(newFileFolder);
                }

                //add size in bytes to parent folders
                request.Size = 0;
                foreach (var file in request.Files)
                    request.Size += file.Length;
                long? filesSizeInBytes = request.Size;
                string path = request.StoragePath;
                AddBytesToServerFolder(path, filesSizeInBytes);

                //add size in bytes to server drive
                AddBytesToServerDrive(drive, filesSizeInBytes);
            }
            else
            {
                //add folder
                string shortPath = request.StoragePath;
                string path = Path.Combine(shortPath, request.Name);
                request.FullStoragePath = path;
                var parentId = GetFolderId(shortPath, driveName);
                var id = Guid.NewGuid();

                var folder = _serverFolderRepository.Find(null).Items?.Where(q => q.StoragePath == request.FullStoragePath && q.IsDeleted == false).FirstOrDefault();
                if (folder != null)
                    throw new EntityAlreadyExistsException($"Folder with name {request.Name} already exists at path {request.StoragePath}");

                Guid? organizationId = _organizationManager.GetDefaultOrganization().Id;

                ServerFolder serverFolder = new ServerFolder()
                {
                    Id = id,
                    ParentFolderId = parentId,
                    CreatedBy = _httpContextAccessor.HttpContext.User.Identity.Name,
                    CreatedOn = DateTime.UtcNow,
                    Name = request.Name,
                    SizeInBytes = 0,
                    StorageDriveId = drive.Id,
                    StoragePath = path,
                    OrganizationId = organizationId
                };

                bool directoryExists = CheckDirectoryExists(shortPath);
                if (directoryExists)
                {
                    //create directory and add server folder
                    _directoryManager.CreateDirectory(path);
                    _serverFolderRepository.Add(serverFolder);
                    _webhookPublisher.PublishAsync("Files.NewFolderCreated", serverFolder.Id.ToString(), serverFolder.Name);

                    var hasChild = false;
                    newFileFolder = newFileFolder.Map(serverFolder, request.StoragePath, hasChild);
                    fileFolderList.Add(newFileFolder);
                }
                else
                    throw new DirectoryNotFoundException("Storage path could not be found");
            }
            return fileFolderList;
        }

        public void AddBytesToServerDrive(ServerDrive serverDrive, long? size)
        {
            //add to storage size in bytes property in server drive
            serverDrive.StorageSizeInBytes += size;
            _serverDriveRepository.Update(serverDrive);
            _webhookPublisher.PublishAsync("Files.DriveUpdated", serverDrive.Id.ToString(), serverDrive.Name);
        }

        public void AddBytesToServerFolder(string path, long? size)
        {
            var pathArray = path.Split(Path.DirectorySeparatorChar);
            List<Guid?> parentIds = GetParentIds(pathArray);
            foreach (var serverFolderId in parentIds)
            {
                var folder = _serverFolderRepository.Find(null).Items?.Where(q => q.Id == serverFolderId).FirstOrDefault();
                if (folder != null)
                {
                    folder.SizeInBytes += size;
                    _serverFolderRepository.Update(folder);
                }
            }
        }

        public FileFolderViewModel SaveFile(FileFolderViewModel request, IFormFile file, ServerDrive drive)
        {
            Guid? id = Guid.NewGuid();
            string shortPath = request.StoragePath;
            string path = request.FullStoragePath;
            Guid? organizationId = _organizationManager.GetDefaultOrganization().Id;

            var checkFile = _serverFileRepository.Find(null)?.Items?.Where(q => q.StoragePath == path).FirstOrDefault();
            if (checkFile != null)
                throw new EntityAlreadyExistsException($"File with name {request.Name} already exists at path {request.StoragePath}");

            //upload file to local server
            bool directoryExists = CheckDirectoryExists(shortPath);
            if (!directoryExists)
                throw new DirectoryNotFoundException("Storage path could not be found");

            if (file.Length <= 0 || file.Equals(null)) throw new Exception("No file exists");
            if (file.Length > 0)
            {
                using (var stream = new FileStream(path, FileMode.Create))
                    file.CopyTo(stream);

                ConvertToBinaryObject(path);
            }

            Guid? folderId = GetFolderId(shortPath, drive.Name);
            var hash = GetHash(path);
            Guid? driveId;
            if (drive != null)
                driveId = drive.Id;
            else throw new EntityDoesNotExistException("Drive could not be found");

            //add file properties to server file entity
            var serverFile = new ServerFile()
            {
                Id = id,
                ContentType = file.ContentType,
                CreatedBy = _httpContextAccessor.HttpContext.User.Identity.Name,
                CreatedOn = DateTime.UtcNow,
                HashCode = hash,
                Name = file.FileName,
                SizeInBytes = file.Length,
                StorageFolderId = folderId,
                StoragePath = path,
                StorageProvider = Configuration["Files:StorageProvider"],
                OrganizationId = organizationId,
                ServerDriveId = drive.Id
            };
            _serverFileRepository.Add(serverFile);
            _webhookPublisher.PublishAsync("Files.NewFileCreated", serverFile.Id.ToString(), serverFile.Name);

            //add file attribute entities
            var attributes = new Dictionary<string, int>()
            {
                { FileAttributes.StorageCount.ToString(), 1 },
                { FileAttributes.RetrievalCount.ToString(), 0 },
                { FileAttributes.AppendCount.ToString(), 0 }
            };

            List<FileAttribute> fileAttributes = new List<FileAttribute>();
            foreach (var attribute in attributes)
            {
                var fileAttribute = new FileAttribute()
                {
                    ServerFileId = id,
                    AttributeValue = attribute.Value,
                    CreatedBy = _httpContextAccessor.HttpContext.User.Identity.Name,
                    CreatedOn = DateTime.UtcNow,
                    DataType = attribute.Value.GetType().ToString(),
                    Name = attribute.Key,
                    OrganizationId = organizationId,
                    ServerDriveId = driveId
                };
                _fileAttributeRepository.Add(fileAttribute);
                fileAttributes.Add(fileAttribute);
            }

            var viewModel = new FileFolderViewModel();
            viewModel = viewModel.Map(serverFile, shortPath);
            return viewModel;
        }

        public void UpdateFile(UpdateServerFileViewModel request)
        {
            Guid entityId = (Guid)request.Id;
            var file = request.File;
            string path = request.StoragePath;
            Guid? organizationId = _organizationManager.GetDefaultOrganization().Id;
            var serverFile = _serverFileRepository.GetOne(entityId);
            if (serverFile == null) throw new EntityDoesNotExistException("Server file could not be found");
            long? size = serverFile.SizeInBytes;
            var hash = GetHash(path);

            //update file attribute entities
            List<FileAttribute> fileAttributes = new List<FileAttribute>();
            var attributes = _fileAttributeRepository.Find(null).Items?.Where(q => q.ServerFileId == entityId);
            if (attributes != null)
            {
                if (hash != serverFile.HashCode)
                {
                    foreach (var attribute in attributes)
                    {
                        if (attribute.Name == FileAttributes.AppendCount.ToString() || attribute.Name == FileAttributes.StorageCount.ToString())
                        {
                            attribute.AttributeValue += 1;

                            _fileAttributeRepository.Update(attribute);
                        }
                        fileAttributes.Add(attribute);
                    }
                }
            }
            else throw new EntityDoesNotExistException("File attribute entities could not be found for this file");

            //update server file entity properties
            serverFile.ContentType = file.ContentType;
            serverFile.HashCode = hash;
            serverFile.Name = file.FileName;
            serverFile.OrganizationId = organizationId;
            serverFile.SizeInBytes = file.Length;
            serverFile.StorageFolderId = request.StorageFolderId;
            serverFile.StoragePath = request.StoragePath;
            serverFile.StorageProvider = request.StorageProvider;
            serverFile.FileAttributes = fileAttributes;

            _serverFileRepository.Update(serverFile);
            _webhookPublisher.PublishAsync("Files.FileUpdated", serverFile.Id.ToString(), serverFile.Name);

            //update file stored in server
            bool directoryExists = CheckDirectoryExists(path);
            if (!directoryExists)
                throw new DirectoryNotFoundException("Storage path could not be found");

            path = Path.Combine(path, request.Id.ToString());

            if (file.Length > 0 && hash != serverFile.HashCode)
            {
                IOFile.Delete(path);
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    file.CopyTo(stream);
                }

                ConvertToBinaryObject(path);
            }

            //update size in bytes in server drive
            var drive = GetDriveById(serverFile.ServerDriveId);
            size = request.SizeInBytes - size;
            AddBytesToServerDrive(drive, size);
        }

        public void DeleteFile(ServerFile serverFile)
        {
            //remove file attribute entities
            var attributes = _fileAttributeRepository.Find(null).Items?.Where(q => q.ServerFileId == serverFile.Id);
            if (attributes != null)
            {
                foreach (var attribute in attributes)
                    _fileAttributeRepository.Delete((Guid)attribute.Id);
            }

            //remove file
            IOFile.Delete(serverFile.StoragePath);

            //remove server file entity
            _serverFileRepository.SoftDelete((Guid)serverFile.Id);
            _webhookPublisher.PublishAsync("Files.FileDeleted", serverFile.Id.ToString(), serverFile.Name);

            //update size in bytes in folder
            var size = -serverFile.SizeInBytes;
            AddBytesToServerFolder(serverFile.StoragePath, size);

            //update size in bytes in server drive
            var drive = GetDriveById(serverFile.ServerDriveId);
            AddBytesToServerDrive(drive, size);
        }

        protected enum FileAttributes
        {
            StorageCount,
            RetrievalCount,
            AppendCount
        }

        protected bool CheckDirectoryExists(string path)
        {
            if (_directoryManager.Exists(path))
                return true;
            else
                return false;
        }

        protected string GetHash(string path)
        {
            string hash = string.Empty;
            byte[] bytes = IOFile.ReadAllBytes(path);
            using (SHA256 sha256Hash = SHA256.Create())
            {
                HashAlgorithm hashAlgorithm = sha256Hash;
                byte[] data = hashAlgorithm.ComputeHash(bytes);
                var sBuilder = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                    sBuilder.Append(data[i].ToString("x2"));
                hash = sBuilder.ToString();
            }
            return hash;
        }

        protected void ConvertToBinaryObject(string filePath)
        {
            byte[] bytes = IOFile.ReadAllBytes(filePath);
            IOFile.WriteAllBytes(filePath, bytes);
        }

        public FileFolderViewModel GetFileFolderViewModel(string id, string driveName)
        {
            ServerFolder folder = new ServerFolder();
            var fileFolder = new FileFolderViewModel();

            Guid? driveId = GetDriveId(driveName);
            var file = _serverFileRepository.Find(null).Items?.Where(q => q.Id.ToString() == id && q.ServerDriveId == driveId).FirstOrDefault();

            if (file != null)
            {
                var serverFolder = _serverFolderRepository.Find(null).Items?.Where(q => q.Id == file.StorageFolderId).FirstOrDefault();
                Guid? folderId = Guid.Empty;
                string storagePath = string.Empty;
                if (serverFolder != null)
                {
                    folderId = serverFolder.Id;
                    storagePath = folder.StoragePath;
                }
                else
                    storagePath = GetDriveById(file.ServerDriveId).Name;

                fileFolder.Id = file.Id;
                fileFolder.Name = file.Name;
                fileFolder.ContentType = file.ContentType;
                fileFolder.StoragePath = storagePath;
                fileFolder.CreatedBy = file.CreatedBy;
                fileFolder.CreatedOn = file.CreatedOn;
                fileFolder.UpdatedOn = file.UpdatedOn;
                fileFolder.FullStoragePath = file.StoragePath;
                fileFolder.Size = file.SizeInBytes;
                fileFolder.HasChild = false;
                fileFolder.IsFile = true;
                fileFolder.ParentId = file.StorageFolderId;
                fileFolder.StorageDriveId = driveId;
            }
            else
            {
                folder = _serverFolderRepository.Find(null).Items?.Where(q => q.Id.ToString() == id && q.StorageDriveId == driveId).FirstOrDefault();

                if (folder == null)
                    throw new EntityDoesNotExistException($"File or folder does not exist");

                var pathArray = folder.StoragePath.Split("\\");
                var shortPathArray = new string[pathArray.Length - 1];
                for (int i = 0; i < pathArray.Length - 1; i++)
                {
                    string folderName = pathArray[i];
                    shortPathArray.SetValue(folderName, i);
                }

                bool hasChild = CheckFolderHasChild(folder.Id);

                fileFolder.Id = folder.Id;
                fileFolder.Name = folder.Name;
                fileFolder.ContentType = "Folder";
                fileFolder.StoragePath = string.Join("\\", shortPathArray);
                fileFolder.CreatedBy = folder.CreatedBy;
                fileFolder.CreatedOn = folder.CreatedOn;
                fileFolder.FullStoragePath = folder.StoragePath;
                fileFolder.Size = folder.SizeInBytes;
                fileFolder.HasChild = hasChild;
                fileFolder.IsFile = false;
                fileFolder.ParentId = folder.ParentFolderId;
                fileFolder.StorageDriveId = driveId;
            }

            return fileFolder;
        }

        public Guid? GetDriveId(string driveName)
        {
            ServerDrive drive = GetDriveByName(driveName);
            Guid? driveId;
            if (drive != null)
                driveId = drive.Id;
            else throw new EntityDoesNotExistException($"Drive {driveName} does not exist");

            return driveId;
        }

        public int? GetFileCount(string driveName)
        {
            Guid? driveId = GetDriveId(driveName);
            var files = _serverFileRepository.Find(null).Items?.Where(q => q.ServerDriveId == driveId);
            int? count = files.Count();
            return count;
        }

        public int? GetFolderCount(string driveName)
        {
            Guid? driveId = GetDriveId(driveName);
            var folders = _serverFolderRepository.Find(null).Items?.Where(q => q.StorageDriveId == driveId);
            int? count = folders.Count();
            return count;
        }

        public ServerFolder GetFolder(string name)
        {
            var serverFolder = _serverFolderRepository.Find(null).Items?.Where(q => q.Name.ToLower() == name.ToLower()).FirstOrDefault();
            if (serverFolder == null)
                return null;
            return serverFolder;
        }

        public Guid? GetFolderId(string path, string driveName)
        {
            string[] pathArray = path.Split(Path.DirectorySeparatorChar);
                string folderName = pathArray[pathArray.Length - 1];
                var folder = GetFolder(folderName);
                Guid? folderId = folder?.Id;
                if (folderId == null)
                {
                    var serverDrive = GetDriveByName(driveName);
                    if (serverDrive != null)
                        folderId = serverDrive.Id;
                    else throw new EntityDoesNotExistException("Drive could not be found");
                }

            return folderId;
        }

        public ServerDrive GetDriveById(Guid? id)
        {
            var serverDrive = _serverDriveRepository.Find(null).Items?.Where(q => q.Id == id).FirstOrDefault();

            if (serverDrive == null)
                throw new EntityDoesNotExistException("Server drive could not be found");

            return serverDrive;
        }

        public ServerDrive GetDriveByName(string name)
        {
            var serverDrive = _serverDriveRepository.Find(null).Items?.Where(q => q.Name == name).FirstOrDefault();
            return serverDrive;
        }

        public void DeleteFolder(ServerFolder folder)
        {
            //delete folder in directory
            _directoryManager.Delete(folder.StoragePath);

            //delete folder in database
            _serverFolderRepository.SoftDelete((Guid)folder.Id);
            _webhookPublisher.PublishAsync("Files.FolderDeleted", folder.Id.ToString(), folder.Name);
        }

        public List<Guid?> GetParentIds(string[] pathArray)
        {
            List<Guid?> parentIds = new List<Guid?>();
            foreach (var folderName in pathArray)
            {
                var folder = _serverFolderRepository.Find(null).Items?.Where(q => q.Name.ToLower() == folderName.ToLower()).FirstOrDefault();
                if (folder != null)
                {
                    Guid? folderId = folder?.Id;
                    Guid? driveId = folder.StorageDriveId;
                    if (folderName == "Files")
                        folderId = driveId;
                    if (folderId != null)
                        parentIds.Add(folderId);
                }
            }

            return parentIds;
        }

        public async Task<FileFolderViewModel> ExportFile(string id, string driveName)
        {
            Guid entityId = Guid.Parse(id);
            Guid? driveId = GetDriveId(driveName);
            var file = _serverFileRepository.GetOne(entityId);
            var folder = _serverFolderRepository.GetOne(entityId);
            bool isFile = true;

            if (file == null && folder == null)
                throw new EntityDoesNotExistException("No file or folder found to export");

            if (file == null && folder != null)
                isFile = false;

            var fileFolder = new FileFolderViewModel();

            if (isFile)
            {
                if (driveId != file.ServerDriveId) throw new EntityDoesNotExistException($"File {file.Name} does not exist in current drive {driveName}");

                var auditLog = new AuditLog()
                {
                    ChangedFromJson = null,
                    ChangedToJson = JsonConvert.SerializeObject(file),
                    CreatedBy = _httpContextAccessor.HttpContext.User.Identity.Name,
                    CreatedOn = DateTime.UtcNow,
                    ExceptionJson = "",
                    ParametersJson = "",
                    ObjectId = file.Id,
                    MethodName = "Download",
                    ServiceName = ToString()
                };

                _auditLogRepository.Add(auditLog);

                //export file
                fileFolder.StoragePath = file.StoragePath;
                fileFolder.Name = file.Name;
                fileFolder.ContentType = file.ContentType;
                fileFolder.Content = new FileStream(fileFolder?.StoragePath, FileMode.Open, FileAccess.Read);

                //update file attribute: retrieval count
                var retrievalFileAttribute = _fileAttributeRepository.Find(null).Items?.Where(q => q.ServerFileId == file.Id && q.Name == FileAttributes.RetrievalCount.ToString()).FirstOrDefault();
                if (retrievalFileAttribute != null)
                {
                    retrievalFileAttribute.AttributeValue += 1;
                    _fileAttributeRepository.Update(retrievalFileAttribute);
                }
            }
            else
                throw new EntityOperationException("Folders cannot be exported at this time");

            return fileFolder;
        }

        public bool CheckFolderHasChild(Guid? id)
        {
            bool hasChild = true;
            var children = _serverFolderRepository.Find(null).Items?.Where(q => q.ParentFolderId == id);
           if (!children.Any())
                hasChild = false;

            return hasChild;
        }

        public void DeleteFileFolder(string id, string driveName = null)
        {
            var drive = GetDriveByName(driveName);
            Guid? driveId = Guid.Empty;
            if (drive != null)
                driveId = drive.Id;
            else
                throw new EntityDoesNotExistException("Drive could not be found");

            var serverFile = _serverFileRepository.Find(null).Items?.Where(q => q.Id.ToString() == id && q.ServerDriveId == driveId).FirstOrDefault();
            var serverFolder = new ServerFolder();
            if (serverFile != null)
                DeleteFile(serverFile);
            else if (serverFile == null)
            {
                serverFolder = _serverFolderRepository.Find(null).Items?.Where(q => q.Id.ToString() == id && q.StorageDriveId == driveId).FirstOrDefault();
                if (serverFolder != null)
                    DeleteFolder(serverFolder);
                else
                    throw new EntityDoesNotExistException($"Folder with id {id} could not be found");
            }
            else 
                throw new EntityDoesNotExistException($"File with id {id} could not be found");
        }

        public FileFolderViewModel RenameFileFolder(string id, string name, string driveName = null)
        {
            var fileFolder = new FileFolderViewModel();
            return fileFolder;
        }
    }
}