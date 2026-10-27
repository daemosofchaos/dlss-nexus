#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef PackageDir
  #define PackageDir "..\dist\DLSS-Nexus-" + AppVersion + "-win-x64"
#endif
#ifndef InstallerAppId
  #define InstallerAppId "{{D7347703-D3D9-48B9-A106-253171DFDE71}"
#endif
#ifndef OutputName
  #define OutputName "DLSS-Nexus-" + AppVersion + "-installer"
#endif
#ifndef InstallerGroupName
  #define InstallerGroupName "DLSS Nexus"
#endif

[Setup]
AppId={#InstallerAppId}
AppName=DLSS Nexus
AppVersion={#AppVersion}
AppVerName=DLSS Nexus {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoDescription=DLSS Nexus installer
DefaultDirName={localappdata}\Programs\DLSS Nexus
DefaultGroupName={#InstallerGroupName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=115
SetupIconFile=..\app\Assets\Brand\nexus.ico
UninstallDisplayIcon={app}\DLSS-Nexus.exe
UninstallDisplayName=DLSS Nexus
OutputDir=..\dist
OutputBaseFilename={#OutputName}
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter=DLSS-Nexus.exe
RestartApplications=no
SetupLogging=yes
DisableWelcomePage=no
DisableDirPage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PackageDir}\DLSS-Nexus.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\SAFETY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DLSS Nexus"; Filename: "{app}\DLSS-Nexus.exe"
Name: "{group}\Uninstall DLSS Nexus"; Filename: "{uninstallexe}"
Name: "{autodesktop}\DLSS Nexus"; Filename: "{app}\DLSS-Nexus.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DLSS-Nexus.exe"; Description: "Open DLSS Nexus"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel1=DLSS Nexus
WelcomeLabel2=Install DLSS Nexus on this computer.%n%nClose any running copy of Nexus before continuing.
FinishedLabel=DLSS Nexus is installed.

; Intentionally no [UninstallDelete] section: retain game files, backups,
; shared cache and any files not owned by this application's installer.