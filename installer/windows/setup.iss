; Library Management System — Windows installer
;
; A branded setup wizard for the desktop application: the maker's mark on the
; welcome and finish pages, a choice of a free trial or a licence key, install
; to Program Files with a data folder the running user may actually write to,
; shortcuts, and a launch at the end.
;
; Build it with Inno Setup 6:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\windows\setup.iss
; The compiled Setup.exe lands in dist\.

#define AppName        "Library Management System"
#define AppVersion     "1.0"
#define AppPublisher   "Tactical Code"
#define AppURL         "https://www.tacticalcode.in"
#define AppExe         "Library Manager.exe"

[Setup]
AppId={{8E2C6A21-7B4D-4E0A-9C55-4C0D0B0A1F01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\Tactical Code\Library Manager
DefaultGroupName=Library Management System
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=Library Manager {#AppVersion} Setup
SetupIconFile=app.ico
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Dirs]
; The records live here, and the application writes to them every day — the
; loans, the fines, its own log and its backups. Program Files is read-only to
; an ordinary user, so this one folder is granted write for the Users group;
; without it the first issue of a book would fail with "database is locked".
Name: "{app}\data"; Permissions: users-modify

[Files]
Source: "..\..\publish\staging\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Library Management System"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Library Management System"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Library Management System"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open Library Management System now"; Flags: nowait postinstall skipifsilent

[Code]
var
  LicPage: TWizardPage;
  TrialRadio, KeyRadio: TNewRadioButton;
  KeyLabel: TNewStaticText;
  KeyEdit: TNewEdit;
  KeyHint: TNewStaticText;

procedure LicChanged(Sender: TObject);
begin
  KeyEdit.Enabled := KeyRadio.Checked;
  KeyLabel.Enabled := KeyRadio.Checked;
  if KeyRadio.Checked then
    WizardForm.ActiveControl := KeyEdit;
end;

procedure InitializeWizard;
begin
  LicPage := CreateCustomPage(wpSelectTasks,
    'Trial or licence',
    'Start a free trial, or activate this machine with a licence key.');

  TrialRadio := TNewRadioButton.Create(WizardForm);
  TrialRadio.Parent := LicPage.Surface;
  TrialRadio.Left := 0;
  TrialRadio.Top := ScaleY(8);
  TrialRadio.Width := LicPage.SurfaceWidth;
  TrialRadio.Caption := 'Start the 14-day free trial';
  TrialRadio.Checked := True;
  TrialRadio.OnClick := @LicChanged;

  KeyRadio := TNewRadioButton.Create(WizardForm);
  KeyRadio.Parent := LicPage.Surface;
  KeyRadio.Left := 0;
  KeyRadio.Top := TrialRadio.Top + ScaleY(34);
  KeyRadio.Width := LicPage.SurfaceWidth;
  KeyRadio.Caption := 'I have a licence key for this machine';
  KeyRadio.OnClick := @LicChanged;

  KeyLabel := TNewStaticText.Create(WizardForm);
  KeyLabel.Parent := LicPage.Surface;
  KeyLabel.Left := ScaleX(22);
  KeyLabel.Top := KeyRadio.Top + ScaleY(30);
  KeyLabel.Caption := 'Licence key';
  KeyLabel.Enabled := False;

  KeyEdit := TNewEdit.Create(WizardForm);
  KeyEdit.Parent := LicPage.Surface;
  KeyEdit.Left := ScaleX(22);
  KeyEdit.Top := KeyLabel.Top + ScaleY(18);
  KeyEdit.Width := LicPage.SurfaceWidth - ScaleX(22);
  KeyEdit.Enabled := False;

  KeyHint := TNewStaticText.Create(WizardForm);
  KeyHint.Parent := LicPage.Surface;
  KeyHint.Left := ScaleX(22);
  KeyHint.Top := KeyEdit.Top + ScaleY(28);
  KeyHint.Width := LicPage.SurfaceWidth - ScaleX(22);
  KeyHint.AutoSize := False;
  KeyHint.WordWrap := True;
  KeyHint.Height := ScaleY(48);
  KeyHint.Caption := 'A key works only on the machine it was issued for. You can also start the trial now and enter a key later, from the login screen.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = LicPage.ID) and KeyRadio.Checked and (Trim(KeyEdit.Text) = '') then
  begin
    MsgBox('Enter your licence key, or choose the 14-day free trial.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir: String;
begin
  if CurStep = ssPostInstall then
  begin
    if KeyRadio.Checked and (Trim(KeyEdit.Text) <> '') then
    begin
      Dir := ExpandConstant('{app}\data');
      ForceDirectories(Dir);
      SaveStringToFile(Dir + '\licence-key.txt', Trim(KeyEdit.Text), False);
    end;
  end;
end;
