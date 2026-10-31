Imports System.Runtime.InteropServices
Imports System.Text

Public Module GameWindow
    Private Delegate Function EnumWindowsCallback(window As IntPtr, parameter As IntPtr) As Boolean
    <DllImport("user32.dll")> Private Function EnumWindows(callback As EnumWindowsCallback, parameter As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Function GetWindowThreadProcessId(window As IntPtr, ByRef processId As UInteger) As UInteger
    End Function
    <DllImport("user32.dll", CharSet:=CharSet.Unicode)> Private Function GetClassName(window As IntPtr, text As StringBuilder, count As Integer) As Integer
    End Function
    <DllImport("user32.dll")> Private Function IsWindowVisible(window As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Function IsIconic(window As IntPtr) As Boolean
    End Function
    <DllImport("user32.dll")> Private Function ShowWindowAsync(window As IntPtr, command As Integer) As Boolean
    End Function
    <DllImport("user32.dll")> Private Function GetForegroundWindow() As IntPtr
    End Function
    <DllImport("user32.dll")> Private Function SetForegroundWindow(window As IntPtr) As Boolean
    End Function

    Public Function WindowClass(window As IntPtr) As String
        Dim text As New StringBuilder(256)
        If window <> IntPtr.Zero Then GetClassName(window, text, text.Capacity)
        Return text.ToString()
    End Function

    Public Function IsRenderWindowClass(name As String) As Boolean
        ' Observed on the fingerprint-supported build. InitialLoading is only a splash.
        Return String.Equals(name, "NeonClass_41", StringComparison.Ordinal)
    End Function

    Public Function Find(processId As Integer) As IntPtr
        Dim found = IntPtr.Zero
        EnumWindows(Function(window, parameter)
                        Dim owner As UInteger
                        GetWindowThreadProcessId(window, owner)
                        If owner = processId AndAlso IsWindowVisible(window) AndAlso IsRenderWindowClass(WindowClass(window)) Then
                            found = window : Return False
                        End If
                        Return True
                    End Function, IntPtr.Zero)
        Return found
    End Function

    Public Function Find(context As InstallContext) As IntPtr
        For Each game In Process.GetProcessesByName("dirt3_game")
            Using game
                Try
                    If String.Equals(game.MainModule.FileName, Path.Combine(context.GameRoot, "dirt3_game.exe"), StringComparison.OrdinalIgnoreCase) Then
                        Dim window = Find(game.Id)
                        If window <> IntPtr.Zero Then Return window
                    End If
                Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ComponentModel.Win32Exception
                    ' The process can exit while enumerating its window.
                End Try
            End Using
        Next
        Return IntPtr.Zero
    End Function

    Public Function MayActivate(foregroundPid As Integer, launcherPid As Integer, gamePid As Integer) As Boolean
        Return foregroundPid = launcherPid OrElse foregroundPid = gamePid
    End Function

    Public Function Activate(window As IntPtr, Optional explicitRequest As Boolean = False) As Boolean
        If window = IntPtr.Zero OrElse Not IsRenderWindowClass(WindowClass(window)) Then Return False
        Dim gamePid, foregroundPid As UInteger
        GetWindowThreadProcessId(window, gamePid)
        GetWindowThreadProcessId(GetForegroundWindow(), foregroundPid)
        ' Startup must not steal focus if the user switched to another application.
        If Not explicitRequest AndAlso Not MayActivate(CInt(foregroundPid), Environment.ProcessId, CInt(gamePid)) Then Return False
        If IsIconic(window) Then ShowWindowAsync(window, 9) ' SW_RESTORE
        Return SetForegroundWindow(window)
    End Function
End Module
