' Launches Zephyr without showing a console window.
' Finds the built exe under Zephyr.UI\bin\Release relative to this script, so it
' works from any clone location and survives a target-framework change.
' On a fresh clone (nothing built yet), it offers to run first-time setup
' (install.cmd), which installs the .NET 10 SDK if needed and builds Zephyr.
Option Explicit

Dim fso, shell, scriptDir, releaseDir, exePath, answer, installPath
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
releaseDir = fso.BuildPath(scriptDir, "Zephyr.UI\bin\Release")

exePath = FindExe(releaseDir)

If exePath <> "" Then
    ' Everyday path: launch hidden, no SDK check, no measurable startup cost.
    shell.Run """" & exePath & """", 0, False
Else
    answer = MsgBox( _
        "Zephyr isn't set up on this PC yet." & vbCrLf & vbCrLf & _
        "Run first-time setup now? It installs the .NET 10 SDK if needed, " & _
        "then builds Zephyr.", _
        vbYesNo + vbQuestion, "Zephyr - Setup needed")

    If answer = vbYes Then
        installPath = fso.BuildPath(scriptDir, "install.cmd")
        shell.CurrentDirectory = scriptDir
        ' Run setup in a visible console and wait for it to finish.
        shell.Run "cmd /c """ & installPath & """", 1, True
        ' If setup produced the exe, launch it hidden.
        exePath = FindExe(releaseDir)
        If exePath <> "" Then shell.Run """" & exePath & """", 0, False
    End If
End If

' Recursively find the first Zephyr.exe under a folder; returns "" if none.
Function FindExe(folderPath)
    FindExe = ""
    If Not fso.FolderExists(folderPath) Then Exit Function
    Dim folder, subFolder, file
    Set folder = fso.GetFolder(folderPath)
    For Each file In folder.Files
        If LCase(file.Name) = "zephyr.exe" Then
            FindExe = file.Path
            Exit Function
        End If
    Next
    For Each subFolder In folder.SubFolders
        FindExe = FindExe(subFolder.Path)
        If FindExe <> "" Then Exit Function
    Next
End Function
