Public Module Session
    Public Function DesktopStartInfo(context As InstallContext) As ProcessStartInfo
        Dim start As New ProcessStartInfo(Path.Combine(context.GameRoot, "dirt3_game.exe")) With {.UseShellExecute = False, .WorkingDirectory = context.GameRoot}
        For Each key In start.Environment.Keys.Where(Function(k) k.StartsWith("DIRT3VR_", StringComparison.OrdinalIgnoreCase)).ToArray()
            start.Environment.Remove(key)
        Next
        start.Environment.Remove("XR_RUNTIME_JSON")
        start.Environment("SteamAppId") = InstallContext.SteamAppId
        start.Environment("SteamGameId") = InstallContext.SteamAppId
        Return start
    End Function

    Public Function LaunchDesktop(context As InstallContext, settings As LauncherSettings, Optional monitor As Drawing.Size = Nothing) As Process
        If settings.SkipStartupMovies Then Throw New IOException("Startup logo skipping requires the managed startup session.")
        Using lease = InteractiveSession.AcquireLease(context)
        context.RequireClosed() : context.ValidateGame()
        If New OwnedFileTransaction(context).Pending Then Throw New IOException("File recovery is pending. Choose Restore original files before launching.")
        settings.Save(context)
        DesktopGraphics.Apply(context, settings, monitor)
        Dim game = Process.Start(DesktopStartInfo(context))
        Try
            If game Is Nothing Then Throw New IOException("DiRT 3 could not start.")
            WriteLaunchStatus(context, "starting", "Waiting for the rendered game window.", game.Id, DateTime.UtcNow)
            Return game
        Catch
            game?.Dispose()
            Throw
        End Try
        End Using
    End Function

    Public Sub WriteLaunchStatus(context As InstallContext, stage As String, message As String,
                                 processId As Integer, requestedUtc As DateTime,
                                 Optional exitCode As Integer? = Nothing, Optional windowClass As String = "", Optional mode As String = "desktop")
        Try
            SafeFiles.SaveJson(Path.Combine(context.UserRoot, "logs", "last-launch.json"),
                               New With {.schema = 1, .mode = mode, .stage = stage, .requestedUtc = requestedUtc,
                                         .updatedUtc = DateTime.UtcNow, .processId = processId, .gameRoot = context.GameRoot,
                                         .exitCode = exitCode, .windowClass = windowClass, .message = message})
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            ' Logging must not misreport or stop an already-started game.
        End Try
    End Sub
End Module
