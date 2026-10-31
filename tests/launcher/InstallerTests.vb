Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

' Command-line contract used by installer/DiRT3VR.iss.
Module InstallerTests
    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim folder = Path.Combine(root, "installer")
        Dim game = Path.Combine(folder, "game")
        Directory.CreateDirectory(Path.Combine(game, "DiRT3VR"))
        File.WriteAllText(Path.Combine(game, "dirt3_game.exe"), "fixture executable")

        Dim discovered = Path.Combine(folder, "game-path.txt")
        check(DiRT3VR.Program.Main({"--discover", discovered}) = 0 AndAlso File.Exists(discovered), "setup discovery writes the detected game folder")
        Dim text = File.ReadAllText(discovered)
        check(Path.IsPathFullyQualified(text) AndAlso Not text.EndsWith("\"c), "discovered game folder is absolute without a trailing separator")
        check(Discovery.GameFolderNear(Path.Combine(game, "DiRT3VR") & "\") = game AndAlso Discovery.GameFolderNear(game) = game,
              "a launcher installed in the game's DiRT3VR subfolder or the game folder finds the game")
        check(Discovery.GameFolderNear(folder) Is Nothing, "folders without dirt3_game.exe nearby are not treated as the game")

        Dim installError = Path.Combine(folder, "install-error.txt")
        check(DiRT3VR.Program.Main({"--check-install", "--game", game, "--error-log", installError}) = 1 AndAlso
              File.ReadAllText(installError).Contains("not supported"), "setup refuses an unsupported executable and reports why")
        Dim missingError = Path.Combine(folder, "missing-error.txt")
        check(DiRT3VR.Program.Main({"--check-install", "--game", folder, "--error-log", missingError}) = 1 AndAlso
              File.ReadAllText(missingError).Contains("dirt3_game.exe was not found"), "setup refuses a folder without the game")
        If Not InstallContext.GameRunning() Then
            Dim uninstallError = Path.Combine(folder, "uninstall-error.txt")
            check(DiRT3VR.Program.Main({"--check-uninstall", "--game", game, "--error-log", uninstallError}) = 0 AndAlso Not File.Exists(uninstallError),
                  "uninstall is allowed without a running game or pending recovery")
        End If
        Using stream = GetType(LauncherForm).Assembly.GetManifestResourceStream("DiRT3VR.ico")
            check(stream IsNot Nothing AndAlso stream.Length > 0, "launcher embeds the DiRT3VR icon")
        End Using
    End Sub

    ' Windows may be created only after the UI phase configures text rendering.
    Public Sub RunUi(check As Action(Of Boolean, String))
        Using branded As New LauncherForm(), plain As New Form()
            check(Not Pixels(branded.Icon).SequenceEqual(Pixels(plain.Icon)), "launcher windows use the DiRT3VR icon, not the default")
        End Using
    End Sub

    Private Function Pixels(icon As Icon) As Integer()
        Using bitmap = icon.ToBitmap()
            Return Enumerable.Range(0, bitmap.Width * bitmap.Height).Select(Function(i) bitmap.GetPixel(i Mod bitmap.Width, i \ bitmap.Width).ToArgb()).ToArray()
        End Using
    End Function
End Module
