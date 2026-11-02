Imports System.Runtime.InteropServices
Imports System.Text.Json
Imports Microsoft.Win32

' One OpenVR setting, read and written live through SteamVR.
Public Interface ITheaterSetting
    Function GetValue() As Boolean
    Sub SetValue(value As Boolean)
    Sub Remove()
End Interface

' The recovery journal: what the setting was before DiRT3VR turned it off.
Public NotInheritable Class TheaterJournal
    Public Property Schema As Integer = 1
    Public Property Section As String = SteamVrTheater.Section
    Public Property Key As String = SteamVrTheater.Key
    Public Property Requested As Boolean
    Public Property Before As Boolean
    Public Property HadOverride As Boolean
    Public Property [Default] As Boolean

    Public Sub Validate()
        If Schema <> 1 OrElse Section <> SteamVrTheater.Section OrElse Key <> SteamVrTheater.Key OrElse Requested OrElse (Not HadOverride AndAlso Before <> [Default]) Then
            Throw New IOException("The SteamVR theater recovery journal is not recognised.")
        End If
    End Sub
End Class

' SteamVR's automatic desktop theater off during SteamVR VR sessions (#24867b69), ported from GRID2VR's
' tools/steamvr_theater.py: OpenVR's IVRSettings_003 as a background client (it never starts SteamVR), only
' the dashboard/autoShowGameTheater key, a journal written before the change, and the player's value put back
' after the session, keeping any change they made meanwhile.
Public NotInheritable Class SteamVrTheater
    Public Const Section As String = "dashboard"
    Public Const Key As String = "autoShowGameTheater"
    Private Const MaxSettingsBytes As Integer = 1024 * 1024

    Private Sub New()
    End Sub

    Public Shared Function JournalPath(context As InstallContext) As String
        Return Path.Combine(context.UserRoot, "steamvr-theater-pending.json")
    End Function

    Public Shared Function Pending(context As InstallContext) As Boolean
        Return File.Exists(JournalPath(context))
    End Function

    Private Shared Function ReadSettings(file As String) As JsonDocument
        SafeFiles.NoLinks(file)
        If New FileInfo(file).Length > MaxSettingsBytes Then Throw New IOException("A SteamVR settings file is too large: " & file)
        Dim bytes = IO.File.ReadAllBytes(file)
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then bytes = bytes.Skip(3).ToArray()
        Return JsonDocument.Parse(bytes, New JsonDocumentOptions With {.AllowTrailingCommas = True, .CommentHandling = JsonCommentHandling.Skip})
    End Function

    ' The configured value: SteamVR's default (resources\settings\default.vrsettings) unless the player's
    ' steamvr.vrsettings overrides it.
    Public Shared Function Configured(defaultSettings As String, userSettings As String) As TheaterJournal
        Dim fallback As Boolean
        Using document = ReadSettings(defaultSettings)
            Dim group As JsonElement, value As JsonElement
            If Not document.RootElement.TryGetProperty(Section, group) OrElse Not group.TryGetProperty(Key, value) OrElse (value.ValueKind <> JsonValueKind.True AndAlso value.ValueKind <> JsonValueKind.False) Then
                Throw New IOException("SteamVR's default theater setting is missing or not a true/false value.")
            End If
            fallback = value.GetBoolean()
        End Using
        Dim result As New TheaterJournal With {.Default = fallback, .Before = fallback}
        If File.Exists(userSettings) Then
            Using document = ReadSettings(userSettings)
                Dim group As JsonElement, value As JsonElement
                If document.RootElement.ValueKind = JsonValueKind.Object AndAlso document.RootElement.TryGetProperty(Section, group) AndAlso group.ValueKind = JsonValueKind.Object AndAlso group.TryGetProperty(Key, value) Then
                    If value.ValueKind <> JsonValueKind.True AndAlso value.ValueKind <> JsonValueKind.False Then Throw New IOException("Your SteamVR theater setting is not a true/false value.")
                    result.Before = value.GetBoolean() : result.HadOverride = True
                End If
            End Using
        End If
        Return result
    End Function

    ' Turns the theater off for the session; the journal exists before the change. Returns what happened.
    Public Shared Function Begin(api As ITheaterSetting, journal As String, original As TheaterJournal) As String
        If File.Exists(journal) Then Throw New IOException("A SteamVR theater change from an earlier session is still pending. Use Restore original files first.")
        original.Requested = False
        original.Validate()
        If api.GetValue() <> original.Before Then Throw New IOException("SteamVR's live theater setting differs from its saved settings; nothing was changed.")
        If Not original.Before Then Return "already_disabled"
        SafeFiles.SaveJson(journal, original)
        api.SetValue(False)
        If api.GetValue() Then Throw New IOException("SteamVR's desktop theater could not be turned off; the recovery journal was kept.")
        Return "temporarily_disabled"
    End Function

    ' Puts the player's value back and removes the journal. A value that is already back (or changed by the
    ' player meanwhile to on) is kept as it is.
    Public Shared Function Restore(api As ITheaterSetting, journal As String) As String
        If Not File.Exists(journal) Then Return "no_pending_change"
        Dim state = SafeFiles.ReadJson(Of TheaterJournal)(journal)
        state.Validate()
        If Not state.Before Then Throw New IOException("The SteamVR theater journal doesn't describe a change DiRT3VR made.")
        Dim result As String
        If api.GetValue() = state.Before Then
            result = "original_value_already_present"
        Else
            If state.HadOverride Then api.SetValue(state.Before) Else api.Remove()
            If api.GetValue() <> state.Before Then Throw New IOException("SteamVR's desktop theater setting could not be restored; the recovery journal was kept.")
            result = If(state.HadOverride, "restored_override", "restored_default")
        End If
        File.Delete(journal)
        Return result
    End Function

    ' Steam's own folder (for config\steamvr.vrsettings), or Nothing.
    Public Shared Function SteamFolder() As String
        Try
            Using key = Registry.CurrentUser.OpenSubKey("Software\Valve\Steam")
                Dim value = TryCast(key?.GetValue("SteamPath"), String)
                Return If(String.IsNullOrWhiteSpace(value), Nothing, Path.GetFullPath(value))
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.SecurityException OrElse TypeOf ex Is ArgumentException
            Return Nothing
        End Try
    End Function

    ' Before a SteamVR session: the theater off, or a note why it stayed as it is (the launch goes on).
    Public Shared Function Suppress(context As InstallContext, runtime As RuntimeInfo) As String
        If runtime Is Nothing OrElse runtime.Mode <> "steamvr" Then Return ""
        Try
            Dim steamVr = Path.GetDirectoryName(runtime.ManifestPath)
            Dim steam = SteamFolder()
            If steam Is Nothing Then Return "SteamVR's desktop theater was left as it is (Steam's folder wasn't found)."
            Dim original = Configured(Path.Combine(steamVr, "resources", "settings", "default.vrsettings"), Path.Combine(steam, "config", "steamvr.vrsettings"))
            Using api As New OpenVrSettings(steamVr)
                Begin(api, JournalPath(context), original)
                Return ""
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            Return "SteamVR's desktop theater was left as it is: " & ex.Message
        End Try
    End Function

    ' After the session, and from Restore original files: puts the player's value back if DiRT3VR changed it.
    Public Shared Function RestorePending(context As InstallContext, steamVrFolder As String) As String
        If Not Pending(context) Then Return ""
        Try
            Using api As New OpenVrSettings(steamVrFolder)
                Restore(api, JournalPath(context))
            End Using
            Return ""
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is DllNotFoundException OrElse TypeOf ex Is BadImageFormatException OrElse TypeOf ex Is EntryPointNotFoundException
            Return "SteamVR's desktop theater setting is still off: " & ex.Message & " Start SteamVR and use Restore original files."
        End Try
    End Function

    ' OpenVR's settings interface through SteamVR's own openvr_api.dll, as a background application: it needs
    ' SteamVR running and never starts it.
    Public NotInheritable Class OpenVrSettings
        Implements ITheaterSetting, IDisposable

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function InitInternal(ByRef [error] As Integer, applicationType As Integer) As UInteger
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Sub ShutdownInternal()
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)> Private Delegate Function GetGenericInterface(<MarshalAs(UnmanagedType.LPStr)> name As String, ByRef [error] As Integer) As IntPtr
        <UnmanagedFunctionPointer(CallingConvention.StdCall)> Private Delegate Sub SetBoolCall(<MarshalAs(UnmanagedType.LPStr)> section As String, <MarshalAs(UnmanagedType.LPStr)> key As String, <MarshalAs(UnmanagedType.I1)> value As Boolean, ByRef [error] As Integer)
        <UnmanagedFunctionPointer(CallingConvention.StdCall)> Private Delegate Function GetBoolCall(<MarshalAs(UnmanagedType.LPStr)> section As String, <MarshalAs(UnmanagedType.LPStr)> key As String, ByRef [error] As Integer) As <MarshalAs(UnmanagedType.I1)> Boolean
        <UnmanagedFunctionPointer(CallingConvention.StdCall)> Private Delegate Sub RemoveKeyCall(<MarshalAs(UnmanagedType.LPStr)> section As String, <MarshalAs(UnmanagedType.LPStr)> key As String, ByRef [error] As Integer)

        Private Const BackgroundApplication As Integer = 3
        Private library As IntPtr
        Private shutdown As ShutdownInternal
        Private ReadOnly setBool As SetBoolCall
        Private ReadOnly getBool As GetBoolCall
        Private ReadOnly removeKey As RemoveKeyCall

        Public Sub New(steamVrFolder As String)
            Dim dll = Path.Combine(steamVrFolder, "bin", If(Environment.Is64BitProcess, "win64", "win32"), "openvr_api.dll")
            SafeFiles.NoLinks(dll)
            If Not File.Exists(dll) Then Throw New IOException("SteamVR's openvr_api.dll wasn't found: " & dll)
            library = NativeLibrary.Load(dll)
            Try
                Dim init = Marshal.GetDelegateForFunctionPointer(Of InitInternal)(NativeLibrary.GetExport(library, "VR_InitInternal"))
                shutdown = Marshal.GetDelegateForFunctionPointer(Of ShutdownInternal)(NativeLibrary.GetExport(library, "VR_ShutdownInternal"))
                Dim generic = Marshal.GetDelegateForFunctionPointer(Of GetGenericInterface)(NativeLibrary.GetExport(library, "VR_GetGenericInterface"))
                Dim failure As Integer
                init(failure, BackgroundApplication)
                If failure <> 0 Then
                    shutdown = Nothing
                    Throw New IOException("SteamVR isn't running (OpenVR error " & failure & "). Start SteamVR before Launch VR.")
                End If
                Dim table = generic("FnTable:IVRSettings_003", failure)
                If failure <> 0 OrElse table = IntPtr.Zero Then Throw New IOException("SteamVR's settings interface is unavailable (OpenVR error " & failure & ").")
                ' IVRSettings_003 function table: [1] SetBool, [5] GetBool, [10] RemoveKeyInSection.
                setBool = Marshal.GetDelegateForFunctionPointer(Of SetBoolCall)(Marshal.ReadIntPtr(table, 1 * IntPtr.Size))
                getBool = Marshal.GetDelegateForFunctionPointer(Of GetBoolCall)(Marshal.ReadIntPtr(table, 5 * IntPtr.Size))
                removeKey = Marshal.GetDelegateForFunctionPointer(Of RemoveKeyCall)(Marshal.ReadIntPtr(table, 10 * IntPtr.Size))
            Catch
                Dispose()
                Throw
            End Try
        End Sub

        Private Shared Sub Check(failure As Integer)
            If failure <> 0 Then Throw New IOException("SteamVR's theater setting operation failed (OpenVR settings error " & failure & ").")
        End Sub

        Public Function GetValue() As Boolean Implements ITheaterSetting.GetValue
            Dim failure As Integer
            Dim value = getBool(Section, Key, failure)
            Check(failure)
            Return value
        End Function

        Public Sub SetValue(value As Boolean) Implements ITheaterSetting.SetValue
            Dim failure As Integer
            setBool(Section, Key, value, failure)
            Check(failure)
        End Sub

        Public Sub Remove() Implements ITheaterSetting.Remove
            Dim failure As Integer
            removeKey(Section, Key, failure)
            Check(failure)
        End Sub

        ' Releases this client only, never the VR server.
        Public Sub Dispose() Implements IDisposable.Dispose
            shutdown?.Invoke() : shutdown = Nothing
            If library <> IntPtr.Zero Then NativeLibrary.Free(library) : library = IntPtr.Zero
        End Sub
    End Class
End Class
