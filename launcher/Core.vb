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
    ' AI difficulty with opponents: -1 = the player's own DiRT 3 setting, 0 (easiest) to 5 (hardest).
    Public Property DirectDifficulty As Integer = -1
    ' Optional controller/wheel button; machine-specific, so never part of settings profiles.
    Public Property RecenterDevice As RecenterDeviceBinding

    Public Sub Validate()
        If Version <> 1 Then Throw New IOException("Unsupported launcher settings version. The existing settings were preserved.")
        If RuntimeMode = "openxr" Then RuntimeMode = "vdxr" ' Migrate the initial research build's system-runtime choice.
        If RuntimeMode <> "vdxr" AndAlso RuntimeMode <> "steamvr" Then Throw New IOException("Choose VDXR or SteamVR.")
        If SteamRuntimeManifest Is Nothing OrElse SteamRuntimeManifest.Length > 32768 Then Throw New IOException("Invalid SteamVR runtime path.")
        If VdxrRuntimeManifest Is Nothing OrElse VdxrRuntimeManifest.Length > 32768 Then Throw New IOException("Invalid VDXR runtime path.")
        If VrRenderScale < 50 OrElse VrRenderScale > 300 Then Throw New IOException("Choose a VR render scale between 50% and 300%.")
        If Not RecenterShortcut.Valid(RecenterKey, RecenterModifiers) Then Throw New IOException("Choose another recenter key. Windows shortcuts such as Alt+Tab are reserved.")
        RecenterDevice?.Validate()
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
