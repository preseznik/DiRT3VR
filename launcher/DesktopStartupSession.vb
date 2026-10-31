' Only desktop launches with temporary startup edits need a process-lifetime owner.
' Ordinary desktop launching remains unchanged and never injects the VR proxy.
Public NotInheritable Class DesktopStartupSession
    Private ReadOnly context As InstallContext
    Private ReadOnly lease As FileStream
    Public ReadOnly Game As Process

    Private Sub New(install As InstallContext, child As Process, sessionLease As FileStream)
        context = install : Game = child : lease = sessionLease
    End Sub

    Public Shared Async Function StartAsync(context As InstallContext, settings As LauncherSettings, monitor As Drawing.Size) As Task(Of DesktopStartupSession)
        If Not settings.SkipStartupMovies Then Throw New ArgumentException("This session requires startup logo skipping.")
        Dim lease = InteractiveSession.AcquireLease(context), game As Process = Nothing
        Dim transaction As New OwnedFileTransaction(context), preparing = False
        Dim failure As Exception = Nothing
        Try
            context.RequireClosed() : context.ValidateGame() : settings.Validate()
            If transaction.Pending Then Throw New IOException("Restore pending session files before launching.")
            Dim changes As New Dictionary(Of String, Byte()) From {{"startup", StartupMovies.ReadPatch(context)}}
            settings.Save(context)
            DesktopGraphics.Apply(context, settings, monitor)
            preparing = True
            transaction.Prepare(changes, apply:=False)
            Await RecoveryWorker.ApplyAsync(context)
            context.RequireClosed()
            game = Process.Start(Session.DesktopStartInfo(context))
            If game Is Nothing Then Throw New IOException("DiRT 3 could not start.")
            Session.WriteLaunchStatus(context, "starting", "Waiting for the game window. Startup definitions will be restored after exit.", game.Id, DateTime.UtcNow)
            Return New DesktopStartupSession(context, game, lease)
        Catch ex As Exception
            failure = ex
        End Try
        ' Once started, an error recording progress must never abandon the child.
        If game IsNot Nothing Then Return New DesktopStartupSession(context, game, lease)
        Try
            If preparing AndAlso transaction.Pending Then Await RecoveryWorker.RecoverAsync(context)
        Catch recoveryError As Exception
            Throw New IOException(failure.Message & " Recovery is pending: " & recoveryError.Message, failure)
        Finally
            lease.Dispose()
        End Try
        Throw failure
    End Function

    Public Async Function WaitAndRestoreAsync() As Task(Of String)
        Try
            Await Game.WaitForExitAsync()
            Dim exitCode = Game.ExitCode
            Await RecoveryWorker.RecoverAsync(context)
            Return "DiRT 3 closed (exit code 0x" & exitCode.ToString("X8") & "). Original startup files restored."
        Finally
            Game.Dispose() : lease.Dispose()
        End Try
    End Function
End Class
