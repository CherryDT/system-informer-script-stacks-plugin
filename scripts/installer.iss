; Build via package.ps1. The managed per-user payload contains the uninstaller
; and repair tools; the plugin itself goes into the chosen System Informer.
#ifndef PackageDir
  #error PackageDir must name the prepared release payload.
#endif
#define ProductName "Script Stacks for System Informer"
#define ProductVersion "0.1.0"

[Setup]
AppId={{903605C5-CEAF-4469-AC52-AB41CC4F127C}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher=David Trapp / Trapp Innovations
DefaultDirName={localappdata}\Programs\SystemInformerScriptStacks
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
WizardStyle=modern
LicenseFile={#PackageDir}\LICENSE
OutputBaseFilename=ScriptStacks-{#ProductVersion}-setup-x64
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\ScriptStacks.dll
SetupLogging=yes
UsePreviousTasks=no

[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[INI]
Filename: "{app}\installation.ini"; Section: "Installation"; Key: "SystemInformerDirectory"; String: "{code:GetTargetDirectory}"; Flags: uninsdeleteentry uninsdeletesectionifempty

[UninstallDelete]
Type: files; Name: "{app}\installation.ini"

[Code]
#include "installer-worker.iss"

var
  TargetPage: TInputDirWizardPage;
  PluginPage: TWizardPage;
  PluginCheck, ProtectionCheck: TNewCheckBox;
  PluginAlready, ProtectionAlready, PluginDetails, ProtectionDetails,
    ProtectionWarning, PluginPathLabel: TNewStaticText;
  PluginPathEdit: TNewEdit;
  PluginSettingsPath, ProtectionSettingsPath: String;
  PluginSettingsSupported, PluginSettingsNeeded, ProtectionSettingsSupported,
    ProtectionSettingsNeeded, PluginSettingsInspected, MissingSettingsConfirmed: Boolean;
  ResetPluginPolicy, RestoreImageProtection, ResetPluginAvailable, RestoreProtectionAvailable: Boolean;
  HostKphEnabled: Boolean;
  ExistingTarget, TargetDirectory: String;
  InstallFailed: Boolean;

function DetectSystemInformer: String;
var Candidate, Key: String;
begin
  Result := ExpandConstant('{pf64}\SystemInformer');
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SystemInformer';
  if RegQueryStringValue(HKLM64, Key, 'InstallLocation', Candidate) and
    FileExists(AddBackslash(Candidate) + 'SystemInformer.exe') then Result := Candidate
  else if RegQueryStringValue(HKCU, Key, 'InstallLocation', Candidate) and
    FileExists(AddBackslash(Candidate) + 'SystemInformer.exe') then Result := Candidate;
end;

function GetTargetDirectory(Param: String): String;
begin
  Result := TargetDirectory;
end;

procedure LoadingChoiceChanged(Sender: TObject);
begin
  MissingSettingsConfirmed := False;
end;

procedure InitializeWizard;
var Notice: TNewStaticText; OverridePath: String;
begin
  ExistingTarget := GetPreviousData('SystemInformerDirectory', '');
  TargetDirectory := ExistingTarget;
  if TargetDirectory = '' then TargetDirectory := DetectSystemInformer;
  OverridePath := ExpandConstant('{param:SYSTEMINFORMERDIR|}');
  if OverridePath <> '' then TargetDirectory := OverridePath;
  TargetPage := CreateInputDirPage(wpLicense, 'System Informer installation',
    'Choose the installation to add Script Stacks to.',
    'Select the folder containing SystemInformer.exe. Close that instance before installing.', False, '');
  TargetPage.Add('System Informer folder:');
  TargetPage.Values[0] := TargetDirectory;
  Notice := TNewStaticText.Create(TargetPage);
  Notice.Parent := TargetPage.Surface;
  Notice.SetBounds(0, ScaleY(110), TargetPage.SurfaceWidth, ScaleY(110));
  Notice.AutoSize := False;
  Notice.WordWrap := True;
  Notice.Caption := 'The plugin and its helper will be added to the selected installation. ' +
    'Private language tools are downloaded only when requested by the plugin.' + #13#10#13#10 +
    'Your other System Informer settings will be preserved.';
  PluginPage := CreateCustomPage(TargetPage.ID, 'System Informer plugin loading',
    'Choose the settings required to load Script Stacks.');
  PluginCheck := TNewCheckBox.Create(PluginPage);
  PluginCheck.Parent := PluginPage.Surface;
  PluginCheck.SetBounds(0, 0, PluginPage.SurfaceWidth, ScaleY(22));
  PluginCheck.Caption := 'Enable third-party plugins in System Informer';
  PluginCheck.OnClick := @LoadingChoiceChanged;
  PluginAlready := TNewStaticText.Create(PluginPage);
  PluginAlready.Parent := PluginPage.Surface;
  PluginAlready.SetBounds(0, 0, PluginPage.SurfaceWidth, ScaleY(22));
  PluginAlready.Caption := 'Third-party plugin loading is already enabled.';
  PluginDetails := TNewStaticText.Create(PluginPage);
  PluginDetails.Parent := PluginPage.Surface;
  PluginDetails.SetBounds(0, ScaleY(26), PluginPage.SurfaceWidth, ScaleY(43));
  PluginDetails.AutoSize := False;
  PluginDetails.WordWrap := True;
  PluginDetails.ShowAccelChar := False;
  ProtectionCheck := TNewCheckBox.Create(PluginPage);
  ProtectionCheck.Parent := PluginPage.Surface;
  ProtectionCheck.SetBounds(0, ScaleY(76), PluginPage.SurfaceWidth, ScaleY(22));
  ProtectionCheck.Caption := 'Allow untrusted plugins (disable image-load protection)';
  ProtectionCheck.OnClick := @LoadingChoiceChanged;
  ProtectionAlready := TNewStaticText.Create(PluginPage);
  ProtectionAlready.Parent := PluginPage.Surface;
  ProtectionAlready.SetBounds(0, ScaleY(76), PluginPage.SurfaceWidth, ScaleY(22));
  ProtectionAlready.Caption := 'Untrusted plugin loading is already enabled in settings.';
  ProtectionDetails := TNewStaticText.Create(PluginPage);
  ProtectionDetails.Parent := PluginPage.Surface;
  ProtectionDetails.SetBounds(0, ScaleY(102), PluginPage.SurfaceWidth, ScaleY(58));
  ProtectionDetails.AutoSize := False;
  ProtectionDetails.WordWrap := True;
  ProtectionDetails.ShowAccelChar := False;
  ProtectionWarning := TNewStaticText.Create(PluginPage);
  ProtectionWarning.Parent := PluginPage.Surface;
  ProtectionWarning.SetBounds(0, ScaleY(164), PluginPage.SurfaceWidth, ScaleY(62));
  ProtectionWarning.AutoSize := False;
  ProtectionWarning.WordWrap := True;
  ProtectionWarning.Font.Style := [fsBold];
  ProtectionWarning.Caption := 'Warning: untrusted DLLs can load into System Informer, and its ' +
    'kernel-driver access is reduced. Some Windows inspection and control features become unavailable.';
  PluginPathLabel := TNewStaticText.Create(PluginPage);
  PluginPathLabel.Parent := PluginPage.Surface;
  PluginPathLabel.SetBounds(0, ScaleY(238), PluginPage.SurfaceWidth, ScaleY(18));
  PluginPathLabel.Caption := 'Settings file:';
  PluginPathEdit := TNewEdit.Create(PluginPage);
  PluginPathEdit.Parent := PluginPage.Surface;
  PluginPathEdit.SetBounds(0, ScaleY(258), PluginPage.SurfaceWidth, ScaleY(23));
  PluginPathEdit.ReadOnly := True;
end;

function ValidateTarget: String;
begin
  Result := '';
  TargetDirectory := RemoveBackslashUnlessRoot(Trim(TargetPage.Values[0]));
  if not FileExists(AddBackslash(TargetDirectory) + 'SystemInformer.exe') then
    Result := 'Choose the folder containing SystemInformer.exe.'
  else if (ExistingTarget <> '') and
    (CompareText(RemoveBackslashUnlessRoot(ExistingTarget), TargetDirectory) <> 0) then
    Result := 'Script Stacks is registered for ' + ExistingTarget + '.' + #13#10 +
      'Uninstall it before selecting a different System Informer installation.';
end;

procedure QueryHostSetting(Operation: String; Reset: Boolean;
  var Supported, Needed: Boolean; var SettingsPath, Diagnostic: String);
var Report, Script, Params: String; ExitCode: Integer;
begin
  if IsUninstaller then Script := ExpandConstant('{app}\setup-settings.ps1')
  else begin
    ExtractTemporaryFile('setup-settings.ps1');
    Script := ExpandConstant('{tmp}\setup-settings.ps1');
  end;
  Report := ExpandConstant('{tmp}\settings-' + Operation + '.ini');
  DeleteFile(Report);
  Params := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    NativeQuote(Script) + ' -Mode Inspect -Operation ' + Operation +
    ' -SystemInformerDirectory ' + NativeQuote(TargetDirectory) +
    ' -ResultFile ' + NativeQuote(Report);
  if Reset then Params := Params + ' -Reset';
  Supported := False;
  Needed := True;
  Diagnostic := 'Could not inspect these settings. Configure them manually in System Informer.';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    if (ExitCode = 0) and FileExists(Report) then begin
      Supported := GetIniString('Plugins', 'Supported', '0', Report) = '1';
      if Supported then Needed := GetIniString('Plugins', 'NeedsChange', '1', Report) = '1';
    end;
    Diagnostic := GetIniString('Plugins', 'Message', Diagnostic, Report);
  end;
  SettingsPath := GetIniString('Plugins', 'Path', '', Report);
  if Operation = 'ImageLoadProtection' then
    HostKphEnabled := GetIniString('Plugins', 'KphEnabled', '1', Report) <> '0';
end;

function InstallOptionDefault(Parameter: String): Boolean;
begin
  { Unattended installs need explicit consent for host preference changes. }
  Result := (ExpandConstant('{param:' + Parameter + '|}') <> '0') and
    (not WizardSilent or (ExpandConstant('{param:' + Parameter + '|}') = '1'));
end;

procedure InspectPluginSettings;
var Diagnostic: String;
begin
  QueryHostSetting('PluginLoading', False, PluginSettingsSupported, PluginSettingsNeeded,
    PluginSettingsPath, Diagnostic);
  PluginCheck.Visible := PluginSettingsNeeded;
  PluginCheck.Enabled := PluginSettingsSupported;
  PluginCheck.Checked := PluginSettingsSupported and PluginSettingsNeeded and
    InstallOptionDefault('ENABLETHIRDPARTYPLUGINS');
  PluginAlready.Visible := PluginSettingsSupported and not PluginSettingsNeeded;
  PluginDetails.Visible := PluginSettingsNeeded;
  if PluginSettingsSupported then
    PluginDetails.Caption := 'Required for Script Stacks. Enables plugin loading and discovery of third-party DLLs.'
  else PluginDetails.Caption := Diagnostic;

  QueryHostSetting('ImageLoadProtection', False, ProtectionSettingsSupported, ProtectionSettingsNeeded,
    ProtectionSettingsPath, Diagnostic);
  ProtectionCheck.Visible := ProtectionSettingsNeeded;
  ProtectionCheck.Enabled := ProtectionSettingsSupported;
  ProtectionCheck.Checked := ProtectionSettingsSupported and ProtectionSettingsNeeded and
    (ExpandConstant('{param:ALLOWUNTRUSTEDPLUGINS|0}') = '1');
  ProtectionAlready.Visible := ProtectionSettingsSupported and not ProtectionSettingsNeeded;
  if HostKphEnabled then
    ProtectionAlready.Caption := 'Untrusted plugin loading is already enabled in settings.'
  else ProtectionAlready.Caption := 'Not required (KPH disabled)';
  ProtectionDetails.Visible := ProtectionSettingsNeeded;
  if ProtectionSettingsSupported then
    ProtectionDetails.Caption := 'System Informer''s driver module (KPH) does not trust this plugin yet. ' +
      'It uses its own allowlist; regular code signing alone is insufficient. This unsigned plugin ' +
      'requires this option when KPH is enabled.'
  else ProtectionDetails.Caption := Diagnostic;
  ProtectionWarning.Visible := ProtectionSettingsNeeded;
  PluginPathEdit.Text := PluginSettingsPath;
  PluginPathEdit.Visible := PluginSettingsPath <> '';
  PluginPathLabel.Visible := PluginPathEdit.Visible;
  MissingSettingsConfirmed := False;
  PluginSettingsInspected := True;
end;

function MissingLoadingOptions: String;
begin
  Result := '';
  if PluginSettingsNeeded and not PluginCheck.Checked then
    Result := ' - Third-party plugin loading';
  if ProtectionSettingsNeeded and not ProtectionCheck.Checked then
    Result := Result + #13#10 + ' - Permission to load this plugin with the kernel driver';
  Result := Trim(Result);
end;

function ConfirmMissingLoadingOptions: Boolean;
var Missing, Message: String;
begin
  Missing := MissingLoadingOptions;
  Result := True;
  if (Missing = '') or MissingSettingsConfirmed then exit;
  if ExpandConstant('{param:ALLOWMANUALSETTINGS|0}') = '1' then begin
    MissingSettingsConfirmed := True;
    exit;
  end;
  Result := False;
  if WizardSilent then exit;
  Message := 'The following required options are unchecked:' + #13#10 + Missing + #13#10#13#10 +
    'Script Stacks will be installed but will fail to load in the protected System Informer instance ' +
    'unless the required settings are enabled manually later.';
  if not PluginSettingsSupported or not ProtectionSettingsSupported then
    Message := 'Setup cannot verify or configure all required plugin-loading settings.' + #13#10#13#10 +
      'Script Stacks will be installed but may fail to load until those settings are configured manually.';
  Result := SuppressibleMsgBox(Message + #13#10#13#10 + 'Do you really want to continue?',
    mbError, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  MissingSettingsConfirmed := Result;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Problem: String;
begin
  Result := True;
  if CurPageID = TargetPage.ID then begin
    Problem := ValidateTarget;
    Result := Problem = '';
    if not Result then SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK)
    else InspectPluginSettings;
  end else if CurPageID = PluginPage.ID then
    Result := ConfirmMissingLoadingOptions;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Also validate when silent installation skips the directory page. }
  Result := ValidateTarget;
  if (Result = '') and not PluginSettingsInspected then InspectPluginSettings;
  if (Result = '') and not ConfirmMissingLoadingOptions then
    Result := 'Required plugin-loading settings were not accepted. Enable the required options, ' +
      'or use /ALLOWMANUALSETTINGS=1 to install and configure them manually later.';
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'SystemInformerDirectory', TargetDirectory);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'System Informer folder:' + NewLine + Space + TargetDirectory + NewLine + NewLine;
  Result := Result + 'Install Script Stacks and its helper.';
  if PluginCheck.Checked then
    Result := Result + NewLine + NewLine + 'Enable third-party plugins in System Informer.';
  if ProtectionCheck.Checked then
    Result := Result + NewLine + 'Allow untrusted plugins by disabling kernel-driver image-load protection.';
  if MissingLoadingOptions <> '' then
    Result := Result + NewLine + NewLine + 'Required loading settings will need manual configuration.';
end;

function RunSetupPhase(Mode, Phase, Target, SettingsPath: String): Boolean;
var Silent: Boolean;
begin
  Result := False;
  if IsUninstaller then Silent := UninstallSilent else Silent := WizardSilent;
  repeat
    if RunSetupWorker(Mode, Phase, Target, SettingsPath) then begin
      Result := True;
      exit;
    end;
    { Retry only the failed phase. Earlier work and the user's choices remain
      intact. An explicit cancellation or unattended run must not prompt again. }
    if (WorkResult = 2) or Silent then exit;
  until SuppressibleMsgBox(Trim(WorkDiagnostics) + #13#10#13#10 +
    'Correct the problem, then click Retry to continue. Cancel stops setup; completed changes are kept.',
    mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    { Inno has committed its repair payload and uninstall record. External
      updates cannot be rolled back, so retain both if any phase fails. }
    InstallFailed := not RunSetupPhase('Install', 'Windows', TargetDirectory, '');
    if not InstallFailed and PluginCheck.Checked then
      InstallFailed := not RunSetupPhase('Install', 'PluginSettings', TargetDirectory, PluginSettingsPath);
    if not InstallFailed and ProtectionCheck.Checked then
      InstallFailed := not RunSetupPhase('Install', 'ImageLoadProtection', TargetDirectory, ProtectionSettingsPath);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and InstallFailed then begin
    WizardForm.FinishedHeadingLabel.Caption := 'Setup needs attention';
    WizardForm.FinishedLabel.Caption := 'Some operations failed or were cancelled. ' +
      'Completed changes and the uninstaller have been kept. Run setup again to retry.';
  end else if (CurPageID = wpFinished) and ProtectionCheck.Checked then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The image-load protection change requires System Informer''s driver module (KPH) to be reloaded. ' +
      'Close all System Informer instances and reload the driver, or restart Windows. ' +
      'See the README for restoring protection.';
end;

function GetCustomSetupExitCode: Integer;
begin
  if InstallFailed then Result := 10 else Result := 0;
end;

function ConfirmToolRemoval: Boolean;
var Form: TSetupForm; Box, ResetBox, RestoreBox: TNewCheckBox; LabelText: TNewStaticText;
  Button: TNewButton;
  Supported, Needed: Boolean;
  SettingsPath, Diagnostic: String;
  FormHeight, OptionY: Integer;
begin
  Result := ExpandConstant('{param:REMOVETOOLS|0}') = '1';
  QueryHostSetting('PluginLoading', True, Supported, Needed, SettingsPath, Diagnostic);
  ResetPluginAvailable := Supported and Needed;
  QueryHostSetting('ImageLoadProtection', True, Supported, Needed, SettingsPath, Diagnostic);
  RestoreProtectionAvailable := Supported and Needed;
  ResetPluginPolicy := ResetPluginAvailable and (ExpandConstant('{param:RESETPLUGINPOLICY|0}') = '1');
  RestoreImageProtection := RestoreProtectionAvailable and (ExpandConstant('{param:RESTOREIMAGELOADPROTECTION|0}') = '1');
  if UninstallSilent then exit;
  FormHeight := 260;
  if ResetPluginAvailable or RestoreProtectionAvailable then FormHeight := FormHeight + 74;
  if ResetPluginAvailable then FormHeight := FormHeight + 28;
  if RestoreProtectionAvailable then FormHeight := FormHeight + 28;
  Form := NewSetupForm(520, FormHeight);
  try
    Form.Caption := 'Uninstall Script Stacks for System Informer';
    LabelText := TNewStaticText.Create(Form);
    LabelText.Parent := Form;
    LabelText.SetBounds(ScaleX(12), ScaleY(12), ScaleX(496), ScaleY(46));
    LabelText.AutoSize := False;
    LabelText.WordWrap := True;
    LabelText.Caption := 'Remove Script Stacks from:' + #13#10 + TargetDirectory;
    Box := TNewCheckBox.Create(Form);
    Box.Parent := Form;
    Box.SetBounds(ScaleX(12), ScaleY(70), ScaleX(496), ScaleY(24));
    Box.Caption := 'Remove downloaded private language tools';
    Box.Checked := Result;
    LabelText := TNewStaticText.Create(Form);
    LabelText.Parent := Form;
    LabelText.SetBounds(ScaleX(30), ScaleY(104), ScaleX(478), ScaleY(106));
    LabelText.AutoSize := False;
    LabelText.WordWrap := True;
    LabelText.Caption := 'Downloaded tools are stored for this Windows user. ' +
      'Keep them to reuse them if you reinstall Script Stacks.' + #13#10#13#10 +
      'Other plugin files and preferences will be kept.';
    OptionY := 215;
    ResetBox := TNewCheckBox.Create(Form);
    ResetBox.Parent := Form;
    ResetBox.SetBounds(ScaleX(12), ScaleY(OptionY), ScaleX(496), ScaleY(22));
    ResetBox.Caption := 'Reset third-party plugin loading (built-in plugins only)';
    ResetBox.Checked := ResetPluginPolicy;
    ResetBox.Visible := ResetPluginAvailable;
    if ResetPluginAvailable then OptionY := OptionY + 28;
    RestoreBox := TNewCheckBox.Create(Form);
    RestoreBox.Parent := Form;
    RestoreBox.SetBounds(ScaleX(12), ScaleY(OptionY), ScaleX(496), ScaleY(22));
    RestoreBox.Caption := 'Re-enable kernel-driver image-load protection';
    RestoreBox.Checked := RestoreImageProtection;
    RestoreBox.Visible := RestoreProtectionAvailable;
    if RestoreProtectionAvailable then OptionY := OptionY + 28;
    LabelText := TNewStaticText.Create(Form);
    LabelText.Parent := Form;
    LabelText.SetBounds(ScaleX(30), ScaleY(OptionY + 2), ScaleX(478), ScaleY(68));
    LabelText.AutoSize := False;
    LabelText.WordWrap := True;
    LabelText.Visible := ResetPluginAvailable or RestoreProtectionAvailable;
    LabelText.Caption := 'These resets can prevent other third-party plugins from loading. ' +
      'Image-load protection changes take effect after System Informer''s driver module (KPH) is reloaded. ' +
      'Unselected settings stay unchanged.';
    Button := TNewButton.Create(Form);
    Button.Parent := Form;
    Button.SetBounds(ScaleX(318), ScaleY(FormHeight - 38), ScaleX(90), ScaleY(26));
    Button.Caption := 'Uninstall';
    Button.Default := True;
    Button.ModalResult := mrOk;
    Button := TNewButton.Create(Form);
    Button.Parent := Form;
    Button.SetBounds(ScaleX(418), ScaleY(FormHeight - 38), ScaleX(90), ScaleY(26));
    Button.Caption := 'Cancel';
    Button.Cancel := True;
    Button.ModalResult := mrCancel;
    if Form.ShowModal <> mrOk then Abort;
    Result := Box.Checked;
    ResetPluginPolicy := ResetBox.Checked;
    RestoreImageProtection := RestoreBox.Checked;
  finally
    Form.Free;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var RemoveTools, Supported, Needed: Boolean; SettingsPath, Diagnostic: String;
begin
  if CurUninstallStep = usUninstall then begin
    TargetDirectory := GetIniString('Installation', 'SystemInformerDirectory', '',
      ExpandConstant('{app}\installation.ini'));
    if TargetDirectory = '' then begin
      SuppressibleMsgBox('The saved System Informer folder is missing. Run setup to repair this installation.', mbError, MB_OK, IDOK);
      Abort;
    end;
    RemoveTools := ConfirmToolRemoval;
    { Abort before Inno removes its own files/registration on failure. This
      allows a failed or cancelled operation to be retried. }
    if not RunSetupPhase('Uninstall', 'Windows', TargetDirectory, '') then begin
      Abort;
    end;
    if ResetPluginPolicy then begin
      QueryHostSetting('PluginLoading', True, Supported, Needed, SettingsPath, Diagnostic);
      if not RunSetupPhase('Uninstall', 'ResetPluginSettings', TargetDirectory, SettingsPath) then Abort;
    end;
    if RestoreImageProtection then begin
      QueryHostSetting('ImageLoadProtection', True, Supported, Needed, SettingsPath, Diagnostic);
      if not RunSetupPhase('Uninstall', 'RestoreImageLoadProtection', TargetDirectory, SettingsPath) then Abort;
    end;
    if RemoveTools and not RunSetupPhase('Uninstall', 'RemoveTools', TargetDirectory, '') then begin
      Abort;
    end;
  end;
end;
