; Inno Setup script for AgentUsageObserver
; Requires Inno Setup 6.x — https://jrsoftware.org/isinfo.php

#define AppName      "Agent Usage Observer"
#define AppVersion   "0.1.3"
#define AppPublisher "maxamo"
#define AppExeName   "AgentUsageObserver.exe"
#define PublishDir   "AgentUsageObserver\bin\Release\net8.0-windows\win-x64\publish"
#define AppIcon      "logo.ico"

[Setup]
AppId={{A3F2E1B4-7C6D-4E9A-8B3F-1D2C5E7A9F0B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer_output
OutputBaseFilename=AgentUsageObserver-{#AppVersion}-Setup
SetupIconFile={#AppIcon}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; Requiere Windows 10 o superior (build 10240+)
MinVersion=10.0.10240
; Cierra la app si está corriendo (necesario para el auto-update /SILENT que
; sobrescribe el ejecutable). RestartApplications la vuelve a abrir al terminar.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startupentry"; Description: "Iniciar {#AppName} con Windows"; GroupDescription: "Opciones adicionales:"; Flags: unchecked

[Files]
; Todos los archivos y subcarpetas del publish (runtime incluido)
Source: "{#PublishDir}\*";    DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#AppIcon}";         DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";             Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIcon}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}";     Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIcon}"

[Registry]
; Inicio con Windows (opcional, solo si el usuario marcó la tarea)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupentry

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Iniciar {#AppName} ahora"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; Cierra la app antes de desinstalar (si está corriendo)
Filename: "taskkill.exe"; Parameters: "/f /im {#AppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
