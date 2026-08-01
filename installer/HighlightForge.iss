#ifndef PublishDir
  #error PublishDir must point to the self-contained HighlightForge publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "artifacts"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{92F1B2DC-0B61-4A44-9A17-EE3214C4B8D7}
AppName=HighlightForge
AppVersion={#AppVersion}
AppPublisher=HighlightForge
DefaultDirName={localappdata}\Programs\HighlightForge
DefaultGroupName=HighlightForge
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=HighlightForge-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HighlightForge.App.exe
VersionInfoVersion={#AppVersion}
VersionInfoProductName=HighlightForge
VersionInfoDescription=Local gaming highlights editor

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HighlightForge"; Filename: "{app}\HighlightForge.App.exe"
Name: "{autodesktop}\HighlightForge"; Filename: "{app}\HighlightForge.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HighlightForge.App.exe"; Description: "Launch HighlightForge"; Flags: nowait postinstall skipifsilent
