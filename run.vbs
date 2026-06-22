' Launches Zephyr without showing a console window.
' Computes the exe path relative to this script, so it works from any clone location.
Dim fso, scriptDir, exePath
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
exePath = fso.BuildPath(scriptDir, "Zephyr.UI\bin\Release\net10.0-windows10.0.22621.0\Zephyr.exe")
CreateObject("WScript.Shell").Run """" & exePath & """", 0, False
