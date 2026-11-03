Imports System.Text.Json
Imports System.Text.Json.Serialization

' Portable choices only: never carry machine paths, game saves or detected XR data.
Public Class ProfileValues
    Public Property ModernInterface As Boolean = True
    Public Property RuntimeMode As String = "vdxr"
    Public Property DiagnosticLogging As Boolean
    Public Property ExperimentalVrEnabled As Boolean
    Public Property VrRenderScale As Integer = 100
    Public Property DesktopWidth As Integer
    Public Property DesktopHeight As Integer
    Public Property DesktopDisplayMode As String = "game"
    Public Property DesktopVSync As String = "game"
    Public Property SkipStartupMovies As Boolean
    Public Property RecenterKey As Integer = RecenterShortcut.DefaultKey
    Public Property RecenterModifiers As Integer
    ' Schema 4: graphics quality choices (catalog field → choice); empty = Use game setting
    ' (desktop) or the same as desktop (VR).
    Public Property GraphicsQuality As New Dictionary(Of String, String)
    Public Property VrGraphicsQuality As New Dictionary(Of String, String)
    Public Property LaunchMode As String = "normal"
    Public Property DirectRoute As Integer
    Public Property DirectCar As String = ""
    Public Property DirectLaps As Integer = 1
    Public Property DirectOpponents As Integer
    Public Property DirectOpponentCars As String = "class"
    Public Property DirectDifficulty As Integer = -1
    Public Property DirectAfterFinish As String = "quit"
    Public Property DirectGearbox As String = "automatic"
    Public Property DirectAbs As Boolean = True
    Public Property DirectStability As Boolean
    Public Property DirectCornerBraking As Boolean
    Public Property DirectThrottle As Boolean
    Public Property DirectSteering As Boolean

    Public Shared Function Capture(value As LauncherSettings) As ProfileValues
        value.Validate()
        Return New ProfileValues With {
            .ModernInterface = value.ModernInterface, .RuntimeMode = value.RuntimeMode,
            .DiagnosticLogging = value.DiagnosticLogging, .ExperimentalVrEnabled = value.ExperimentalVrEnabled,
            .VrRenderScale = value.VrRenderScale, .DesktopWidth = value.DesktopWidth, .DesktopHeight = value.DesktopHeight,
            .DesktopDisplayMode = value.DesktopDisplayMode, .DesktopVSync = value.DesktopVSync, .SkipStartupMovies = value.SkipStartupMovies,
            .RecenterKey = value.RecenterKey, .RecenterModifiers = value.RecenterModifiers,
            .GraphicsQuality = New Dictionary(Of String, String)(value.GraphicsQuality),
            .VrGraphicsQuality = New Dictionary(Of String, String)(value.VrGraphicsQuality),
            .LaunchMode = value.LaunchMode, .DirectRoute = value.DirectRoute, .DirectCar = value.DirectCar, .DirectLaps = value.DirectLaps,
            .DirectOpponents = value.DirectOpponents, .DirectOpponentCars = value.DirectOpponentCars, .DirectDifficulty = value.DirectDifficulty, .DirectAfterFinish = value.DirectAfterFinish,
            .DirectGearbox = value.DirectGearbox, .DirectAbs = value.DirectAbs, .DirectStability = value.DirectStability, .DirectCornerBraking = value.DirectCornerBraking,
            .DirectThrottle = value.DirectThrottle, .DirectSteering = value.DirectSteering}
    End Function

    Public Function ApplyTo(local As LauncherSettings) As LauncherSettings
        Dim result As New LauncherSettings With {
            .ModernInterface = ModernInterface, .RuntimeMode = RuntimeMode,
            .SteamRuntimeManifest = local.SteamRuntimeManifest, .VdxrRuntimeManifest = local.VdxrRuntimeManifest, .RecenterDevice = local.RecenterDevice,
            .RecenterKeepLevel = local.RecenterKeepLevel,
            .SeatOffsets = If(local.SeatOffsets, New Dictionary(Of String, SeatOffset)).ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value.Copy()),
            .SeatSameForAll = local.SeatSameForAll, .SeatAllCars = If(local.SeatAllCars, New SeatOffset).Copy(), .StartInCockpit = local.StartInCockpit, .SeatOnDesktop = local.SeatOnDesktop,
            .CockpitSteady = local.CockpitSteady, .CockpitHideDriver = local.CockpitHideDriver, .CockpitHideCoDriver = local.CockpitHideCoDriver, .DesktopFov = local.DesktopFov,
            .HudDistance = local.HudDistance, .HudFollowView = local.HudFollowView, .PerfDisplay = local.PerfDisplay, .PerfFollowView = local.PerfFollowView,
            .SeatMenuKey = local.SeatMenuKey, .SeatMenuModifiers = local.SeatMenuModifiers, .SeatMenuButton = local.SeatMenuButton, .SeatMenuOnDesktop = local.SeatMenuOnDesktop,
            .ToggleVrKey = local.ToggleVrKey, .ToggleVrModifiers = local.ToggleVrModifiers, .ToggleVrDevice = local.ToggleVrDevice,
            .DiagnosticLogging = DiagnosticLogging, .ExperimentalVrEnabled = ExperimentalVrEnabled,
            .VrRenderScale = VrRenderScale, .DesktopWidth = DesktopWidth, .DesktopHeight = DesktopHeight,
            .DesktopDisplayMode = DesktopDisplayMode, .DesktopVSync = DesktopVSync, .SkipStartupMovies = SkipStartupMovies,
            .RecenterKey = RecenterKey, .RecenterModifiers = RecenterModifiers,
            .GraphicsQuality = New Dictionary(Of String, String)(If(GraphicsQuality, New Dictionary(Of String, String))),
            .VrGraphicsQuality = New Dictionary(Of String, String)(If(VrGraphicsQuality, New Dictionary(Of String, String))),
            .LaunchMode = If(LaunchMode, "normal"), .DirectRoute = DirectRoute, .DirectCar = If(DirectCar, ""), .DirectLaps = DirectLaps,
            .DirectOpponents = DirectOpponents, .DirectOpponentCars = If(DirectOpponentCars, "class"), .DirectDifficulty = DirectDifficulty, .DirectAfterFinish = If(DirectAfterFinish, "quit"),
            .DirectGearbox = If(DirectGearbox, "automatic"), .DirectAbs = DirectAbs, .DirectStability = DirectStability, .DirectCornerBraking = DirectCornerBraking,
            .DirectThrottle = DirectThrottle, .DirectSteering = DirectSteering}
        result.Validate()
        Return result
    End Function
End Class

Public Class SettingsProfile
    ' 2 added startup logo skipping, 3 the recenter key, 4 graphics quality.
    Public Const CurrentSchema As Integer = 4
    Public Property Kind As String
    Public Property Schema As Integer
    Public Property Name As String
    Public Property Settings As ProfileValues

    Public Sub Validate()
        If Kind <> "DiRT3VR.SettingsProfile" OrElse (Schema < 1 OrElse Schema > CurrentSchema) Then Throw New IOException("This is not a supported DiRT3VR settings profile.")
        Name = SettingsProfileStore.ValidateName(Name)
        If Settings Is Nothing Then Throw New IOException("The profile has no settings.")
        If Schema = 1 AndAlso Settings.SkipStartupMovies Then Throw New IOException("Startup choices require settings profile schema 2.")
        If Schema < 3 AndAlso (Settings.RecenterKey <> RecenterShortcut.DefaultKey OrElse Settings.RecenterModifiers <> 0) Then Throw New IOException("Recenter keys require settings profile schema 3.")
        If Schema < 4 AndAlso ((Settings.GraphicsQuality IsNot Nothing AndAlso Settings.GraphicsQuality.Count > 0) OrElse (Settings.VrGraphicsQuality IsNot Nothing AndAlso Settings.VrGraphicsQuality.Count > 0)) Then Throw New IOException("Graphics quality choices require settings profile schema 4.")
        If Schema < 4 AndAlso (If(Settings.LaunchMode, "normal") <> "normal" OrElse Settings.DirectRoute <> 0 OrElse If(Settings.DirectCar, "") <> "" OrElse Settings.DirectLaps <> 1 OrElse Settings.DirectOpponents <> 0 OrElse Settings.DirectDifficulty <> -1 OrElse If(Settings.DirectAfterFinish, "quit") <> "quit" OrElse If(Settings.DirectGearbox, "automatic") <> "automatic" OrElse Not Settings.DirectAbs OrElse
                                Settings.DirectStability OrElse Settings.DirectCornerBraking OrElse Settings.DirectThrottle OrElse Settings.DirectSteering) Then Throw New IOException("Direct practice requires settings profile schema 4.")
        Settings.ApplyTo(New LauncherSettings())
    End Sub
End Class

Public Class SettingsProfileEntry
    Public Property Id As String
    Public Property Profile As SettingsProfile
    Public Property Revision As String
    Public Property Problem As String
    Public Overrides Function ToString() As String
        Return If(Profile?.Name, "Unavailable profile (" & Id.Substring(0, 8) & ")")
    End Function
End Class

Public Class SettingsProfileStore
    Private Const MaximumBytes As Integer = 65536
    Private Const MaximumProfiles As Integer = 128
    Private Shared ReadOnly Options As New JsonSerializerOptions With {
        .WriteIndented = True, .PropertyNameCaseInsensitive = True,
        .UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, .MaxDepth = 8}
    Public ReadOnly Folder As String

    Public Sub New(context As InstallContext)
        Folder = Path.Combine(context.UserRoot, "settings-profiles")
    End Sub

    Public Shared Function ValidateName(value As String) As String
        If value Is Nothing Then Throw New IOException("Enter a profile name.")
        value = value.Trim().Normalize()
        If value.Length = 0 OrElse value.Length > 64 OrElse value.Any(Function(c) Char.IsControl(c)) Then
            Throw New IOException("Use a profile name of 1–64 characters without control characters.")
        End If
        Return value
    End Function

    Private Function ProfilePath(id As String) As String
        Dim parsed As Guid
        If Not Guid.TryParseExact(id, "N", parsed) Then Throw New IOException("Invalid settings profile identifier.")
        Return Path.Combine(Folder, parsed.ToString("N") & ".json")
    End Function

    Private Shared Function ReadBytes(filename As String) As Byte()
        SafeFiles.NoLinks(filename)
        Using stream As New FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read)
            If stream.Length > MaximumBytes Then Throw New IOException("The settings profile exceeds 64 KiB.")
            Dim bytes(CInt(stream.Length) - 1) As Byte
            stream.ReadExactly(bytes)
            Return bytes
        End Using
    End Function

    Private Shared Sub CheckDuplicateKeys(element As JsonElement)
        If element.ValueKind <> JsonValueKind.Object Then Return
        Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each item In element.EnumerateObject()
            If Not names.Add(item.Name) Then Throw New IOException("The profile contains a duplicate field: " & item.Name)
            CheckDuplicateKeys(item.Value)
        Next
    End Sub

    Private Shared Function Decode(bytes As Byte()) As SettingsProfile
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then bytes = bytes.Skip(3).ToArray()
        Using document = JsonDocument.Parse(bytes, New JsonDocumentOptions With {.MaxDepth = 8})
            CheckDuplicateKeys(document.RootElement)
        End Using
        Dim profile = JsonSerializer.Deserialize(Of SettingsProfile)(bytes, Options)
        If profile Is Nothing Then Throw New IOException("The settings profile is empty.")
        profile.Validate()
        Return profile
    End Function

    Public Function Read(id As String) As SettingsProfileEntry
        Dim bytes = ReadBytes(ProfilePath(id))
        Return New SettingsProfileEntry With {.Id = id, .Profile = Decode(bytes), .Revision = SafeFiles.HashBytes(bytes)}
    End Function

    Public Function List() As List(Of SettingsProfileEntry)
        SafeFiles.NoLinks(Folder)
        Dim result As New List(Of SettingsProfileEntry)
        If Not Directory.Exists(Folder) Then Return result
        Dim files = Directory.EnumerateFiles(Folder, "*.json").Where(
            Function(filename) System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(filename), "\A[0-9a-fA-F]{32}\z"))
        For Each filename In files.Take(MaximumProfiles + 1)
            If result.Count >= MaximumProfiles Then Throw New IOException("Too many settings profiles. Keep at most 128 in this installation.")
            Dim id = Path.GetFileNameWithoutExtension(filename), parsed As Guid
            If Not Guid.TryParseExact(id, "N", parsed) Then Continue For
            Try
                result.Add(Read(id))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                result.Add(New SettingsProfileEntry With {.Id = id, .Problem = ex.Message})
            End Try
        Next
        Return result.OrderBy(Function(entry) entry.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
    End Function

    Private Function MakeProfile(name As String, values As ProfileValues, Optional exceptId As String = Nothing) As SettingsProfile
        Dim profile As New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = SettingsProfile.CurrentSchema, .Name = name, .Settings = values}
        profile.Validate()
        Dim entries = List()
        If exceptId Is Nothing AndAlso entries.Count >= MaximumProfiles Then Throw New IOException("Keep at most 128 settings profiles per installation.")
        If entries.Any(Function(entry) entry.Id <> exceptId AndAlso String.Equals(entry.Profile?.Name, profile.Name, StringComparison.OrdinalIgnoreCase)) Then
            Throw New IOException("A settings profile with that name already exists. Choose another name or update the selected profile.")
        End If
        Return profile
    End Function

    Public Function Create(name As String, values As ProfileValues) As SettingsProfileEntry
        Dim profile = MakeProfile(name, values), id = Guid.NewGuid().ToString("N")
        SafeFiles.WriteAtomic(ProfilePath(id), JsonSerializer.SerializeToUtf8Bytes(profile, Options))
        Return Read(id)
    End Function

    Private Sub RequireUnchanged(entry As SettingsProfileEntry)
        If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Revision) OrElse SafeFiles.HashBytes(ReadBytes(ProfilePath(entry.Id))) <> entry.Revision Then
            Throw New IOException("The selected profile changed or is unavailable. Refresh the list before trying again.")
        End If
    End Sub

    Public Function Update(entry As SettingsProfileEntry, name As String, values As ProfileValues) As SettingsProfileEntry
        RequireUnchanged(entry)
        Dim profile = MakeProfile(name, values, entry.Id)
        SafeFiles.WriteAtomic(ProfilePath(entry.Id), JsonSerializer.SerializeToUtf8Bytes(profile, Options))
        Return Read(entry.Id)
    End Function

    Public Sub Delete(entry As SettingsProfileEntry)
        RequireUnchanged(entry)
        File.Delete(ProfilePath(entry.Id))
    End Sub

    Public Function Import(filename As String) As SettingsProfileEntry
        Dim profile = Decode(ReadBytes(filename))
        Dim name = profile.Name, suffix = 2
        Dim names = List().Where(Function(entry) entry.Profile IsNot Nothing).Select(Function(entry) entry.Profile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
        Do While names.Contains(name)
            Dim ending = " (" & suffix.ToString() & ")"
            name = profile.Name.Substring(0, Math.Min(profile.Name.Length, 64 - ending.Length)) & ending
            suffix += 1
        Loop
        Return Create(name, profile.Settings)
    End Function

    Public Sub Export(entry As SettingsProfileEntry, filename As String)
        RequireUnchanged(entry)
        Dim destination = Path.GetFullPath(filename)
        Dim storeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder)) & Path.DirectorySeparatorChar
        If destination.StartsWith(storeRoot, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("Export outside the internal settings-profiles folder.")
        SafeFiles.WriteAtomic(destination, JsonSerializer.SerializeToUtf8Bytes(Read(entry.Id).Profile, Options))
    End Sub
End Class
