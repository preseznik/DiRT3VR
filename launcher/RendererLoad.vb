Imports System.Runtime.InteropServices
Imports System.Text
Imports Microsoft.Win32

' DiRT 3 imports d3d11.dll by name, so Windows normally picks DiRT3VR's copy in the game
' folder. When it doesn't, the game runs flat and VR never starts. This names the cause
' (adapted from GRID2VR's renderer-load check in tools/Game-Executable.ps1 and
' tools/User-Session.ps1).
Public NotInheritable Class RendererLoadResult
    Public Property Schema As Integer = 1
    Public Property RendererLoaded As Boolean
    Public Property Reason As String = ""
    Public Property ProxyPresent As Boolean
    Public Property ProxyUnchanged As Boolean
    Public Property LoadedD3d11 As String() = Array.Empty(Of String)()
    Public Property PreferSystem32Images As Boolean?
    Public Property Message As String
End Class

Public NotInheritable Class RendererLoad
    Public Const Loaded As String = "loaded", NotInspected As String = "not_inspected", ProxyMissing As String = "proxy_missing",
                 ProxyChanged As String = "proxy_changed", PreferSystem32Policy As String = "prefer_system32_policy", WindowsD3d11Loaded As String = "windows_d3d11_loaded"

    Private Sub New()
    End Sub

    Private Const ImageFileExecutionOptions As String = "SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
    Private Const QueryInformation As UInteger = &H400UI, QueryLimitedInformation As UInteger = &H1000UI, ReadMemory As UInteger = &H10UI
    Private Const ImageLoadPolicy As Integer = 10, ListModulesAll As UInteger = 3

    <DllImport("kernel32.dll", SetLastError:=True)> Private Shared Function OpenProcess(access As UInteger, inherit As Boolean, id As Integer) As IntPtr
    End Function
    <DllImport("kernel32.dll")> Private Shared Function CloseHandle(handle As IntPtr) As Boolean
    End Function
    <DllImport("kernel32.dll", SetLastError:=True)> Private Shared Function GetProcessMitigationPolicy(process As IntPtr, policy As Integer, ByRef value As UInteger, size As UIntPtr) As Boolean
    End Function
    <DllImport("psapi.dll", SetLastError:=True)> Private Shared Function EnumProcessModulesEx(process As IntPtr, <Out> modules As IntPtr(), size As UInteger, ByRef needed As UInteger, filter As UInteger) As Boolean
    End Function
    <DllImport("psapi.dll", CharSet:=CharSet.Unicode, SetLastError:=True)> Private Shared Function GetModuleFileNameExW(process As IntPtr, [module] As IntPtr, name As StringBuilder, size As UInteger) As UInteger
    End Function

    ' Which cause applies, from what was observed (Nothing for loaded = the modules could not be listed).
    Public Shared Function Classify(proxyPath As String, proxyPresent As Boolean, proxyUnchanged As Boolean, modules As IEnumerable(Of String), preferSystem32 As Boolean?) As String
        If modules Is Nothing Then Return NotInspected
        Dim full = Path.GetFullPath(proxyPath)
        If modules.Any(Function(item) String.Equals(Path.GetFullPath(item), full, StringComparison.OrdinalIgnoreCase)) Then Return Loaded
        If Not proxyPresent Then Return ProxyMissing
        If Not proxyUnchanged Then Return ProxyChanged
        If preferSystem32 = True Then Return PreferSystem32Policy
        Return WindowsD3d11Loaded
    End Function

    ' Player-facing explanation; Nothing when no failure is proven.
    Public Shared Function Message(reason As String) As String
        Select Case reason
            Case ProxyMissing
                Return "DiRT 3 started without DiRT3VR: DiRT3VR's d3d11.dll was removed from the game folder before the game loaded it. This is usually antivirus; check Windows Security → Protection history, allow the file or exclude the game folder, then launch VR again."
            Case ProxyChanged
                Return "DiRT 3 started without DiRT3VR: the d3d11.dll in the game folder was replaced before the game loaded it. Check for another mod or tool that installs a d3d11.dll, then launch VR again."
            Case PreferSystem32Policy
                Return "DiRT 3 started without DiRT3VR: the Windows rule 'prefer system32 images' made the game use Windows' own d3d11.dll. Close DiRT3VR and start it again from its Start menu shortcut, not through another launcher, then launch VR again."
            Case WindowsD3d11Loaded
                Return "DiRT 3 started without DiRT3VR: Windows could not load DiRT3VR's d3d11.dll and used its own. Install the latest Microsoft Visual C++ 2015-2022 Redistributable (x86) and check Windows Security → Protection history for a blocked file, then launch VR again."
            Case Else
                Return Nothing
        End Select
    End Function

    ' Exploit protection's per-program MitigationOptions: bits 60-61 of the first qword are 1 when
    ' "prefer system32 images" is forced on.
    Public Shared Function PreferSystem32Option(options As Byte()) As Boolean
        Return options IsNot Nothing AndAlso options.Length >= 8 AndAlso ((BitConverter.ToUInt64(options, 0) >> 60) And 3UL) = 1UL
    End Function

    Public Shared Function ExploitProtectionForces(executable As String) As Boolean
        Try
            Using key = Registry.LocalMachine.OpenSubKey(Path.Combine(ImageFileExecutionOptions, Path.GetFileName(executable)))
                If key Is Nothing Then Return False
                If PreferSystem32Option(TryCast(key.GetValue("MitigationOptions"), Byte())) Then Return True
                For Each name In key.GetSubKeyNames()
                    Using filter = key.OpenSubKey(name)
                        If filter IsNot Nothing AndAlso String.Equals(TryCast(filter.GetValue("FilterFullPath"), String), executable, StringComparison.OrdinalIgnoreCase) AndAlso
                           PreferSystem32Option(TryCast(filter.GetValue("MitigationOptions"), Byte())) Then Return True
                    End Using
                Next
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.SecurityException OrElse TypeOf ex Is ArgumentException
        End Try
        Return False
    End Function

    ' "Prefer system32 images" (PROCESS_MITIGATION_IMAGE_LOAD_POLICY bit 2); Nothing when Windows can't tell.
    Public Shared Function PrefersSystem32Images(processId As Integer) As Boolean?
        Dim process = OpenProcess(QueryLimitedInformation, False, processId)
        If process = IntPtr.Zero Then Return Nothing
        Try
            Dim flags As UInteger
            If Not GetProcessMitigationPolicy(process, ImageLoadPolicy, flags, New UIntPtr(4UI)) Then Return Nothing
            Return (flags And 4UI) <> 0UI
        Finally
            CloseHandle(process)
        End Try
    End Function

    ' Full paths of the process's loaded modules with this file name; Nothing when they can't be listed.
    Public Shared Function ModulePaths(processId As Integer, name As String) As String()
        Dim process = OpenProcess(QueryInformation Or ReadMemory, False, processId)
        If process = IntPtr.Zero Then Return Nothing
        Try
            Dim needed As UInteger
            Dim moduleHandles(1023) As IntPtr
            If Not EnumProcessModulesEx(process, moduleHandles, CUInt(moduleHandles.Length * IntPtr.Size), needed, ListModulesAll) Then Return Nothing
            Dim count = CInt(Math.Min(needed \ CUInt(IntPtr.Size), CUInt(moduleHandles.Length)))
            Dim result As New List(Of String)
            For i = 0 To count - 1
                Dim buffer As New StringBuilder(1024)
                If GetModuleFileNameExW(process, moduleHandles(i), buffer, CUInt(buffer.Capacity)) = 0UI Then Continue For
                Dim file = buffer.ToString()
                If String.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase) Then result.Add(file)
            Next
            Return result.ToArray()
        Finally
            CloseHandle(process)
        End Try
    End Function

    ' Before a VR launch: the game inherits the launcher's image-load rule, and exploit
    ' protection can force it for dirt3_game.exe. Empty when nothing blocks the launch.
    Public Shared Function LaunchBlock(context As InstallContext) As String
        If ExploitProtectionForces(Path.Combine(context.GameRoot, "dirt3_game.exe")) Then
            Return "DiRT3VR did not start DiRT 3: Windows exploit protection forces 'prefer system32 images' (PreferSystem32) for dirt3_game.exe, so the game would use Windows' own d3d11.dll instead of DiRT3VR's. Remove that exploit-protection setting for dirt3_game.exe, then launch again."
        End If
        If PrefersSystem32Images(Environment.ProcessId) = True Then
            Return "DiRT3VR did not start DiRT 3: DiRT3VR itself was started with the Windows rule 'prefer system32 images', which DiRT 3 would inherit and then use Windows' own d3d11.dll. Close DiRT3VR and start it again from its Start menu shortcut, not through another launcher (if that is Steam, exit Steam completely and start it again first)."
        End If
        Return ""
    End Function

    ' After the game window exists: which d3d11.dll DiRT 3 loaded, saved as renderer-load.json in the session logs.
    Public Shared Function Diagnose(context As InstallContext, processId As Integer, logDirectory As String) As RendererLoadResult
        Dim proxy = Path.GetFullPath(Path.Combine(context.GameRoot, "d3d11.dll"))
        Dim present = File.Exists(proxy)
        Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
        Dim unchanged = present
        Try
            If present AndAlso File.Exists(payload) Then unchanged = SafeFiles.Hash(proxy) = SafeFiles.Hash(payload)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Dim modules = ModulePaths(processId, "d3d11.dll")
        Dim preferSystem32 = PrefersSystem32Images(processId)
        Dim result As New RendererLoadResult With {.ProxyPresent = present, .ProxyUnchanged = unchanged, .LoadedD3d11 = If(modules, Array.Empty(Of String)()), .PreferSystem32Images = preferSystem32,
                                                   .Reason = Classify(proxy, present, unchanged, modules, preferSystem32)}
        result.RendererLoaded = result.Reason = Loaded
        result.Message = Message(result.Reason)
        If logDirectory IsNot Nothing Then
            Try
                SafeFiles.SaveJson(Path.Combine(logDirectory, "renderer-load.json"), result)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' The explanation still reaches the launcher window.
            End Try
        End If
        Return result
    End Function
End Class
