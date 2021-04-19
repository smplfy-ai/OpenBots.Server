import { Component, OnInit, ViewChild } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { BusinessEventService } from '../business-event.service';
import { JsonEditorComponent, JsonEditorOptions } from 'ang-jsoneditor';
import { NbToastrService } from '@nebular/theme';

@Component({
  selector: 'ngx-add-business-event',
  templateUrl: './add-business-event.component.html',
  styleUrls: ['./add-business-event.component.scss']
})
export class AddBusinessEventComponent implements OnInit {
  urlId;
  showRaiseBtn: boolean = false;
  submitted = false;
  createdOn: any = [];
  showallsystemEvent: any = [];
  payloadSchema: any = [];
  businessEventform: FormGroup;
  showChangedToJson: boolean = false;

  title = 'Add';
  public editorOptions: JsonEditorOptions;
  @ViewChild('editor') editor: JsonEditorComponent;

  constructor(
    private acroute: ActivatedRoute,
    private formBuilder: FormBuilder,
    private toastrService: NbToastrService,
    protected router: Router,
    protected BusinessEventservice: BusinessEventService,
  ) {
    this.editorOptions = new JsonEditorOptions();
    this.editorOptions.modes = ['code', 'text', 'tree', 'view'];
    this.urlId = this.acroute.snapshot.params['id'];
  }

  ngOnInit(): void {
    this.businessEventform = this.formBuilder.group({
      name: ['', [
        Validators.required,
        Validators.minLength(3),
        Validators.maxLength(100),
        Validators.pattern('^[A-Za-z0-9_.-]{3,100}$'),
      ],],
      description: ['', [
        Validators.required,
        Validators.minLength(3),
        Validators.maxLength(100)]],
      entityType: ['', [Validators.required, Validators.pattern('^[{]?[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}[}]?$')]],
      payloadSchema: ['', [Validators.required, Validators.minLength(3)]]
    });
    if (this.urlId) {
      this.getBusinessEventID(this.urlId);
      this.title = 'Update';
      this.businessEventform.controls['name'].disable({ onlySelf: true });
      this.showRaiseBtn = true;
    }
  }

  get f() {
    return this.businessEventform.controls;
  }


  onSubmit() {
    if (this.urlId) {
      this.UpdateBusiness();
    } else {
      this.SaveBusiness();
    }
  }

  SaveBusiness() {
    this.submitted = true;
    this.BusinessEventservice.addBusinessEvent(this.businessEventform.value).subscribe(
      () => {
        this.toastrService.success(
          'Business Event Save Successfully!',
          'Success'
        );
        this.router.navigate(['pages/business-event/list']);
      }
    );
  }

  UpdateBusiness() {
    this.submitted = true;
    let obj = {
      name: this.businessEventform.getRawValue().name,
      description: this.businessEventform.value.description,
      entityType: this.businessEventform.value.entityType,
      payloadSchema: this.businessEventform.value.payloadSchema,
    }
    this.BusinessEventservice.updateBusinessEvent(obj, this.urlId).subscribe(
      () => {
        this.toastrService.success(
          'Business Event Save Successfully!',
          'Success'
        );
        this.router.navigate(['pages/business-event/list']);
      }
    );
  }


  raiseBusinessEvent() {
    this.submitted = true;
    let obj = {
      entityId: this.businessEventform.value.entityType,
      entityName: this.businessEventform.getRawValue().name
    }
    this.BusinessEventservice.raiseBusinessEvent(obj, this.urlId).subscribe(
      () => {
        this.toastrService.success(
          'Raise Business Event Save Successfully!',
          'Success'
        );
        //  this.router.navigate(['pages/business-event/list']);
      }
    );
  }

  getBusinessEventID(id) {
    this.BusinessEventservice.getSystemEventid(id).subscribe((data: any) => {
      this.showallsystemEvent = data;
      this.businessEventform.patchValue(data);
      // this.businessEventform.disable();
    });
  }

}
