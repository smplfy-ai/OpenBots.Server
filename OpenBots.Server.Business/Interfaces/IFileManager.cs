﻿using OpenBots.Server.Model.Core;
using OpenBots.Server.Model.File;
using OpenBots.Server.ViewModel.File;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenBots.Server.Business.Interfaces
{
    public interface IFileManager : IManager
    {
        public List<FileFolderViewModel> AddFileFolder(FileFolderViewModel request, string driveName = null);

        //public void UpdateFile(UpdateServerFileViewModel request);

        public void DeleteFileFolder(string id, string driveName = null);

        public int? GetFileCount(string driveName = null);

        public int? GetFolderCount(string driveName = null);

        public FileFolderViewModel GetFileFolder(string path, string driveName = null);

        public ServerDrive GetDrive(string driveName = null);

        public PaginatedList<FileFolderViewModel> GetFilesFolders(bool? isFile, string driveName = null, Predicate<FileFolderViewModel> predicate = null, string sortColumn = "", OrderByDirectionType direction = OrderByDirectionType.Ascending, int skip = 0, int take = 100);

        public Task<FileFolderViewModel> ExportFileFolder(string id, string driveName = null);

        public FileFolderViewModel RenameFileFolder(string id, string name, string driveName = null);

        public FileFolderViewModel MoveFileFolder(string fileFolderId, string parentFolderId, string driveName = null);

        public FileFolderViewModel CopyFileFolder(string fileFolderId, string parentFolderId, string driveName = null);
    }
}
