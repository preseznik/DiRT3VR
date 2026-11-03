Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports Microsoft.Win32

Public Module SafeFiles
    Public ReadOnly JsonOptions As New JsonSerializerOptions With {.WriteIndented = True, .PropertyNameCaseInsensitive = True}

    Public Sub NoLinks(path As String)
        Dim current = IO.Path.GetFullPath(path)
        Do While Not String.IsNullOrEmpty(current)
            Try
                If (File.GetAttributes(current) And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("Linked paths are not supported: " & current)
            Catch ex As FileNotFoundException
                ' A new leaf is expected; its existing parents must still be checked.
            Catch ex As DirectoryNotFoundException
            End Try
            current = IO.Path.GetDirectoryName(current)
        Loop
    End Sub

    Public Function Hash(path As String) As String
        Using stream = File.OpenRead(path)
            Return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
        End Using
    End Function

    Public Function HashBytes(bytes As Byte()) As String
        Return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
    End Function

    Public Function IsReadOnly(path As String) As Boolean
        Return (File.GetAttributes(path) And FileAttributes.ReadOnly) <> 0
    End Function

    Public Sub SetReadOnly(path As String, value As Boolean)
        Dim attributes = File.GetAttributes(path)
        Dim updated = If(value, attributes Or FileAttributes.ReadOnly, attributes And Not FileAttributes.ReadOnly)
        If updated <> attributes Then File.SetAttributes(path, updated)
    End Sub

    ' Attributes that SetFileAttributes accepts; the replacement keeps these from the file it replaces.
    Private Const CopiedAttributes As FileAttributes = FileAttributes.ReadOnly Or FileAttributes.Hidden Or FileAttributes.System Or FileAttributes.Archive Or FileAttributes.NotContentIndexed

    ' Replacing a file keeps its attributes. Players make game settings read-only on
    ' purpose, and Windows refuses to replace a read-only file even for administrators,
    ' so the flag is lifted only for the rename and is back afterwards (adapted from DiRT2VR).
    Public Sub WriteAtomic(path As String, bytes As Byte())
        NoLinks(path)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(IO.Path.GetFullPath(path)))
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            Using stream As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                stream.Write(bytes) : stream.Flush(True)
            End Using
            Dim existing = File.Exists(path)
            Dim wasReadOnly = existing AndAlso IsReadOnly(path)
            If existing Then
                Dim kept = File.GetAttributes(path) And CopiedAttributes
                File.SetAttributes(temporary, If(kept = 0, FileAttributes.Normal, kept))
            End If
            If wasReadOnly Then SetReadOnly(path, False)
            Try
                File.Move(temporary, path, True)
            Finally
                If wasReadOnly AndAlso File.Exists(path) Then SetReadOnly(path, True)
            End Try
        Finally
            If File.Exists(temporary) Then SetReadOnly(temporary, False) : File.Delete(temporary)
        End Try
    End Sub

    Public Sub SaveJson(Of T)(path As String, value As T)
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions))
    End Sub

    Public Function ReadJson(Of T)(path As String) As T
        NoLinks(path)
        If New FileInfo(path).Length > 1024 * 1024 Then Throw New IOException("Data file is too large: " & path)
        Dim bytes = File.ReadAllBytes(path)
        ' Windows PowerShell 5.1 writes this prefix for -Encoding utf8.
        ' System.Text.Json's byte overload expects the JSON itself, without BOM.
        If bytes.Length >= 3 AndAlso bytes(0) = &HEF AndAlso bytes(1) = &HBB AndAlso bytes(2) = &HBF Then bytes = bytes.Skip(3).ToArray()
        Dim value = JsonSerializer.Deserialize(Of T)(bytes, JsonOptions)
        If value Is Nothing Then Throw New IOException("Empty data file: " & path)
        Return value
    End Function
End Module

Public Class InstallContext
    Public Const SupportedHash As String = "c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98"
    Public Const SteamAppId As String = "321040"
    Public ReadOnly GameRoot As String
    Public ReadOnly UserRoot As String
    Public ReadOnly GraphicsPath As String
    Public ReadOnly PackageRoot As String
    Public ReadOnly Id As String

    Public Sub New(root As String, Optional userBase As String = Nothing, Optional graphics As String = Nothing, Optional package As String = Nothing)
        If String.IsNullOrWhiteSpace(root) OrElse Not Path.IsPathFullyQualified(root) Then Throw New ArgumentException("Select an absolute game folder.")
        If userBase IsNot Nothing AndAlso Not Path.IsPathFullyQualified(userBase) Then Throw New ArgumentException("The user data folder must be absolute.")
        If graphics IsNot Nothing AndAlso Not Path.IsPathFullyQualified(graphics) Then Throw New ArgumentException("The graphics file path must be absolute.")
        GameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GameRoot.ToUpperInvariant()))).Substring(0, 24)
        UserRoot = Path.Combine(If(userBase, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiRT3VR")), Id)
        GraphicsPath = If(graphics, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games\DiRT3\hardwaresettings\hardware_settings_config.xml"))
        PackageRoot = Path.GetFullPath(If(package, AppContext.BaseDirectory))
    End Sub

    Public ReadOnly Property PreferencesPath As String
        Get
            Return Path.Combine(UserRoot, "settings.json")
        End Get
    End Property

    Public ReadOnly Property DiagnosticsPath As String
        Get
            Return Path.Combine(UserRoot, "diagnostics", "latest.json")
        End Get
    End Property

    Public Sub ValidateGame()
        SafeFiles.NoLinks(GameRoot)
        Dim executable = Path.Combine(GameRoot, "dirt3_game.exe")
        SafeFiles.NoLinks(executable)
        If Not File.Exists(executable) Then Throw New IOException("dirt3_game.exe was not found. Select the DiRT 3 Complete Edition game folder.")
        If SafeFiles.Hash(executable) <> SupportedHash Then Throw New IOException("This DiRT 3 executable is not supported by this build. No game files were changed.")
    End Sub

    Public Sub RequireClosed()
        If GameRunning() Then Throw New IOException("Close DiRT 3 before launching again or restoring files.")
    End Sub

    Public Shared Function GameRunning() As Boolean
        Dim games = Process.GetProcessesByName("dirt3_game")
        Try
            Return games.Length <> 0
        Finally
            For Each game In games
                game.Dispose()
            Next
        End Try
    End Function
End Class

Public Class LauncherSettings
    Public Property Version As Integer = 1
    Public Property ModernInterface As Boolean = True
    Public Property RuntimeMode As String = "vdxr"
    Public Property SteamRuntimeManifest As String = ""
    Public Property VdxrRuntimeManifest As String = ""
    Public Property DiagnosticLogging As Boolean = False
    Public Property ExperimentalVrEnabled As Boolean = False
    Public Property VrRenderScale As Integer = 100
    Public Property DesktopWidth As Integer = 0
    Public Property DesktopHeight As Integer = 0
    Public Property DesktopDisplayMode As String = "game"
    Public Property DesktopVSync As String = "game"
    Public Property SkipStartupMovies As Boolean = False
    Public Property RecenterKey As Integer = RecenterShortcut.DefaultKey
    Public Property RecenterModifiers As Integer = 0
    ' True: recentering turns to face forward and keeps the horizon level (default, as in GRID2VR).
    ' False: a manual recenter also resets pitch and roll.
    Public Property RecenterKeepLevel As Boolean = True
    ' Graphics quality per catalog field (GraphicsCatalog), applied at every launch; absent = Use game setting.
    Public Property GraphicsQuality As New Dictionary(Of String, String)
    ' VR-only quality per field, applied inside the VR session's temporary changes; absent = same as desktop.
    Public Property VrGraphicsQuality As New Dictionary(Of String, String)
    ' "normal" (DiRT 3's menus) or "direct" (direct practice: straight onto DirectRoute with DirectCar).
    Public Property LaunchMode As String = "normal"
    Public Property DirectRoute As Integer
    Public Property DirectCar As String = ""
    ' Laps for circuit routes (rallycross, Land Rush); point-to-point stages are one run.
    Public Property DirectLaps As Integer = 1
    ' AI opponents on circuits (0 = practice alone) and their cars: "class", "mixed" or "same" (DirectPractice).
    Public Property DirectOpponents As Integer
    Public Property DirectOpponentCars As String = "class"
    ' AI difficulty with opponents: -1 = DiRT 3's default (a direct start doesn't load the player's profile), 0 (easiest) to 5 (hardest).
    Public Property DirectDifficulty As Integer = -1
    ' After the finish: "quit" (DiRT 3 closes, as its benchmark start does) or "menus" (DiRT 3's main menu).
    Public Property DirectAfterFinish As String = "quit"
    ' Direct practice assists and gearbox (a direct start doesn't load the player's profile, so these
    ' start from DiRT 3's defaults: ABS on, the others off, automatic gears).
    Public Property DirectGearbox As String = "automatic"
    Public Property DirectAbs As Boolean = True
    Public Property DirectStability As Boolean
    Public Property DirectCornerBraking As Boolean
    Public Property DirectThrottle As Boolean
    Public Property DirectSteering As Boolean
    ' VR seat positions per catalog car (SeatPositions), or one for every car; they belong to the
    ' player's body and rig, so settings profiles and Restore launcher defaults keep them.
    Public Property SeatOffsets As New Dictionary(Of String, SeatOffset)
    Public Property SeatSameForAll As Boolean
    Public Property SeatAllCars As New SeatOffset
    ' VR: start races in the cockpit view (each car's views before head-cam hidden for the session).
    Public Property StartInCockpit As Boolean
    ' VR cockpit comfort (#cd56379b): the session's cockpit view without DiRT 3's own G-force head movement,
    ' shake and distortion (on by default), and the driver or co-driver hidden. Kept with the seat settings.
    Public Property CockpitSteady As Boolean = True
    Public Property CockpitHideDriver As Boolean
    Public Property CockpitHideCoDriver As Boolean
    ' The cockpit field of view on the monitor (#b330cc4f): degrees added to each car's own, 0 = DiRT 3's.
    ' Launch writes it into the cars' camera files for the session; VR uses the headset's own field of view.
    Public Property DesktopFov As Integer
    ' The VR HUD panel (#3652b9aa; Lens's renderer side): metres away, 1 to 5 in 0.5 m steps (it keeps its size
    ' in view), and whether it follows the view instead of staying where the view was last recentered.
    Public Const DefaultHudDistance As Decimal = 2D
    Public Property HudDistance As Decimal = DefaultHudDistance
    Public Property HudFollowView As Boolean
    ' FPS and GPU usage in the headset (#4896ffd3; Lens's renderer side), and whether that display follows the view.
    Public Property PerfDisplay As Boolean
    Public Property PerfFollowView As Boolean
    ' Anisotropic filtering (#15714dd2; Wrench's renderer side): 0 = DiRT 3's own, 1 = off (trilinear), else 2/4/8/16×.
    ' DiRT 3 has no setting for it, so another choice goes to the renderer as DIRT3VR_ANISOTROPY; on the monitor
    ' that installs DiRT3VR's renderer for the Launch session.
    Public Shared ReadOnly AnisotropyLevels As Integer() = {0, 1, 2, 4, 8, 16}
    Public Property DesktopAnisotropy As Integer
    Public Property VrAnisotropy As Integer
    ' Seat positions on the monitor too (Launch, #b330cc4f): the same camera files, journaled for a desktop session.
    Public Property SeatOnDesktop As Boolean
    ' The in-game seat menu's key (#bd820b32, docs/seat-channel.md): opens it, and saves and closes it; 0 = none.
    ' F11 by default, a key DiRT 3's own keyboard map leaves free. Kept with the other seat settings.
    Public Const DefaultSeatMenuKey As Integer = 122
    Public Property SeatMenuKey As Integer = DefaultSeatMenuKey
    Public Property SeatMenuModifiers As Integer
    ' A controller or wheel button that also opens the seat menu (#c137c681); machine-specific like RecenterDevice.
    Public Property SeatMenuButton As RecenterDeviceBinding
    ' The in-game seat menu on the monitor too (Launch; Lens's renderer side on #35826889): Launch then installs
    ' DiRT3VR's renderer for the session. Off by default; it needs a seat menu key or button.
    Public Property SeatMenuOnDesktop As Boolean
    ' Toggle VR (#288ceec1; Lens's renderer side): switches between stereo and a flat virtual screen while
    ' playing. F6 by default (DiRT2VR's F9 is DiRT 3's Replay UI On Off); 0 = no key. Kept with the seat menu key.
    Public Const DefaultToggleVrKey As Integer = 117
    Public Property ToggleVrKey As Integer = DefaultToggleVrKey
    Public Property ToggleVrModifiers As Integer
    ' An optional controller or wheel button for it; machine-specific like RecenterDevice.
    Public Property ToggleVrDevice As RecenterDeviceBinding
    ' Optional controller/wheel button; machine-specific, so never part of settings profiles.
    Public Property RecenterDevice As RecenterDeviceBinding

    ' Why a key can't open the seat menu, or "" when it can (0 = no key).
    Public Shared Function SeatMenuKeyProblem(key As Integer, modifiers As Integer, recenterKey As Integer, recenterModifiers As Integer) As String
        If key = 0 Then Return ""
        If Not RecenterShortcut.Valid(key, modifiers) OrElse (key >= 37 AndAlso key <= 40) OrElse key = 13 Then Return "Choose another seat menu key. The arrows and Enter move and save in the menu, and Windows shortcuts are reserved."
        If key = recenterKey AndAlso modifiers = recenterModifiers Then Return "The seat menu and recentering need different keys."
        Return ""
    End Function

    ' Why a key can't toggle VR, or "" when it can (0 = no key): never the recenter or seat menu key.
    Public Shared Function ToggleVrKeyProblem(key As Integer, modifiers As Integer, recenterKey As Integer, recenterModifiers As Integer, seatMenuKey As Integer, seatMenuModifiers As Integer) As String
        If key = 0 Then Return ""
        If Not RecenterShortcut.Valid(key, modifiers) Then Return "Choose another Toggle VR key. Windows shortcuts such as Alt+Tab are reserved."
        If key = recenterKey AndAlso modifiers = recenterModifiers Then Return "Toggle VR and recentering need different keys."
        If key = seatMenuKey AndAlso modifiers = seatMenuModifiers Then Return "Toggle VR and the seat menu need different keys."
        Return ""
    End Function

    Public Sub Validate()
        If Version <> 1 Then Throw New IOException("Unsupported launcher settings version. The existing settings were preserved.")
        If RuntimeMode = "openxr" Then RuntimeMode = "vdxr" ' Migrate the initial research build's system-runtime choice.
        If RuntimeMode <> "vdxr" AndAlso RuntimeMode <> "steamvr" Then Throw New IOException("Choose VDXR or SteamVR.")
        If SteamRuntimeManifest Is Nothing OrElse SteamRuntimeManifest.Length > 32768 Then Throw New IOException("Invalid SteamVR runtime path.")
        If VdxrRuntimeManifest Is Nothing OrElse VdxrRuntimeManifest.Length > 32768 Then Throw New IOException("Invalid VDXR runtime path.")
        If VrRenderScale < 50 OrElse VrRenderScale > 300 Then Throw New IOException("Choose a VR render scale between 50% and 300%.")
        If Not RecenterShortcut.Valid(RecenterKey, RecenterModifiers) Then Throw New IOException("Choose another recenter key. Windows shortcuts such as Alt+Tab are reserved.")
        Dim seatKey = SeatMenuKeyProblem(SeatMenuKey, SeatMenuModifiers, RecenterKey, RecenterModifiers)
        If seatKey <> "" Then Throw New IOException(seatKey)
        If SeatMenuKey = 0 Then SeatMenuModifiers = 0
        Dim toggleKey = ToggleVrKeyProblem(ToggleVrKey, ToggleVrModifiers, RecenterKey, RecenterModifiers, SeatMenuKey, SeatMenuModifiers)
        If toggleKey <> "" Then Throw New IOException(toggleKey)
        If ToggleVrKey = 0 Then ToggleVrModifiers = 0
        RecenterDevice?.Validate()
        If SeatMenuButton IsNot Nothing Then
            SeatMenuButton.Validate("seat menu")
            Dim reserved = SeatMenuInput.Reserved(SeatMenuButton)
            If reserved <> "" Then Throw New IOException(reserved)
            If SeatMenuButton.SameInput(RecenterDevice) Then Throw New IOException("The seat menu and recentering need different controller buttons.")
        End If
        If ToggleVrDevice IsNot Nothing Then
            ToggleVrDevice.Validate("Toggle VR")
            If ToggleVrDevice.SameInput(RecenterDevice) OrElse ToggleVrDevice.SameInput(SeatMenuButton) Then Throw New IOException("Toggle VR needs its own controller button, not the recenter or seat menu button.")
        End If
        DesktopGraphics.Validate(Me)
        GraphicsQuality = GraphicsCatalog.Current.Normalize(GraphicsQuality)
        VrGraphicsQuality = GraphicsCatalog.Current.Normalize(VrGraphicsQuality)
        If LaunchMode <> "normal" AndAlso LaunchMode <> "direct" Then Throw New IOException("Choose Normal launch or Direct practice.")
        If DirectRoute <> 0 AndAlso EventCatalog.Current.FindRoute(DirectRoute) Is Nothing Then Throw New IOException("Choose a listed route for direct practice.")
        If DirectCar Is Nothing OrElse (DirectCar <> "" AndAlso EventCatalog.Current.FindCar(DirectCar) Is Nothing) Then Throw New IOException("Choose a listed car for direct practice.")
        If LaunchMode = "direct" AndAlso (DirectRoute = 0 OrElse DirectCar = "") Then Throw New IOException("Choose a route and a car for direct practice.")
        If DirectLaps < 1 OrElse DirectLaps > 5 Then Throw New IOException("Choose 1 to 5 laps for direct practice.")
        If DirectOpponents < 0 OrElse DirectOpponents > DirectPractice.MaxOpponents Then Throw New IOException("Choose 0 to " & DirectPractice.MaxOpponents & " opponents for direct practice.")
        If Not DirectPractice.OpponentCarChoices.Contains(DirectOpponentCars) Then Throw New IOException("Choose which cars the opponents drive.")
        If DirectDifficulty < -1 OrElse DirectDifficulty > DirectPractice.MaxDifficulty Then Throw New IOException("Choose the AI difficulty for direct practice.")
        If Not DirectPractice.AfterFinishChoices.Contains(DirectAfterFinish) Then Throw New IOException("Choose what happens after the finish.")
        If Not DirectPractice.GearboxChoices.Contains(DirectGearbox) Then Throw New IOException("Choose the gearbox for direct practice.")
        SeatAllCars = If(SeatAllCars, New SeatOffset) : SeatAllCars.ClearSessionFlags() : SeatAllCars.Validate()
        If HudDistance < 1D OrElse HudDistance > 5D OrElse HudDistance * 2D <> Decimal.Truncate(HudDistance * 2D) Then Throw New IOException("Choose a VR HUD distance from 1 to 5 m, in 0.5 m steps.")
        If Not AnisotropyLevels.Contains(DesktopAnisotropy) OrElse Not AnisotropyLevels.Contains(VrAnisotropy) Then Throw New IOException("Choose Use game setting, Off, 2×, 4×, 8× or 16× anisotropic filtering.")
        If DesktopFov < -SeatOffset.FovNarrower OrElse DesktopFov > SeatOffset.FovWider Then Throw New IOException("Choose a cockpit field of view between " & SeatOffset.FovNarrower & "° narrower and " & SeatOffset.FovWider & "° wider.")
        Dim seats As New Dictionary(Of String, SeatOffset)(StringComparer.Ordinal)
        For Each pair In If(SeatOffsets, New Dictionary(Of String, SeatOffset)).OrderBy(Function(item) item.Key, StringComparer.Ordinal)
            If EventCatalog.Current.FindCar(pair.Key) Is Nothing Then Throw New IOException("Seat positions name an unknown car.")
            If pair.Value Is Nothing OrElse pair.Value.IsZero Then Continue For
            pair.Value.ClearSessionFlags()
            pair.Value.Validate()
            seats.Add(pair.Key, pair.Value)
        Next
        SeatOffsets = seats
    End Sub

    Public Shared Function Load(context As InstallContext) As LauncherSettings
        If Not File.Exists(context.PreferencesPath) Then Return New LauncherSettings With {.SteamRuntimeManifest = Discovery.SteamRuntime(), .VdxrRuntimeManifest = Discovery.VdxrRuntime()}
        Dim value = SafeFiles.ReadJson(Of LauncherSettings)(context.PreferencesPath)
        value.Validate()
        If String.IsNullOrWhiteSpace(value.VdxrRuntimeManifest) Then value.VdxrRuntimeManifest = Discovery.VdxrRuntime()
        Return value
    End Function

    Public Sub Save(context As InstallContext)
        Validate() : SafeFiles.SaveJson(context.PreferencesPath, Me)
    End Sub
End Class

Public Module Discovery
    Public Function VdxrRuntime() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr-32.json")
    End Function

    Public Function SteamLibraries() As IEnumerable(Of String)
        Dim roots As New List(Of String)
        Try
            Using key = Registry.CurrentUser.OpenSubKey("Software\Valve\Steam")
                Dim path = TryCast(key?.GetValue("SteamPath"), String)
                If Not String.IsNullOrWhiteSpace(path) Then roots.Add(path.Replace("/", "\"))
            End Using
        Catch ex As Exception When TypeOf ex Is Security.SecurityException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Discovery is optional; default install and manual selection remain available.
        End Try
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"))
        For Each root In roots.ToArray()
            Dim libraries = Path.Combine(root, "steamapps", "libraryfolders.vdf")
            Try
                If File.Exists(libraries) Then
                    For Each match As Match In Regex.Matches(File.ReadAllText(libraries), """path""\s+""([^""]+)""")
                        roots.Add(match.Groups(1).Value.Replace("\\", "\"))
                    Next
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' A protected library listing must not prevent selecting a game manually.
            End Try
        Next
        Return roots.Distinct(StringComparer.OrdinalIgnoreCase)
    End Function

    ' Setup installs the launcher in a DiRT3VR subfolder of the game; a portable
    ' copy may also sit in the game folder itself.
    Public Function GameFolderNear(folder As String) As String
        Dim own = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
        For Each candidate In {own, Path.GetDirectoryName(own)}
            If candidate IsNot Nothing AndAlso File.Exists(Path.Combine(candidate, "dirt3_game.exe")) Then Return candidate
        Next
        Return Nothing
    End Function

    Public Function GamePath() As String
        Dim near = GameFolderNear(AppContext.BaseDirectory)
        If near IsNot Nothing Then Return near
        For Each root In SteamLibraries()
            Dim candidate = Path.Combine(root, "steamapps\common\DiRT 3 Complete Edition")
            If File.Exists(Path.Combine(candidate, "dirt3_game.exe")) Then Return candidate
        Next
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam\steamapps\common\DiRT 3 Complete Edition")
    End Function

    Public Function SteamRuntime() As String
        For Each root In SteamLibraries()
            Dim candidate = Path.Combine(root, "steamapps\common\SteamVR\steamxr_win32.json")
            If File.Exists(candidate) Then Return candidate
        Next
        Return ""
    End Function
End Module
