#define MyAppName      "VRCNext"
#define MyAppVersion   "2026.1.0"
#define MyAppPublisher "VRCNext"
#define MyAppURL       "https://vrcnext.app"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=VRCNext_Setup_{#MyAppVersion}_x64
SetupIconFile=logo.ico
WizardImageFile=installer_banner.png
WizardSmallImageFile=installer_small.png
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.19041
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "releases\VRCNext-win-Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\VRCNext-win-Setup.exe"; Flags: waituntilterminated; StatusMsg: "Installing VRCNext..."
