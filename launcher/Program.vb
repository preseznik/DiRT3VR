Imports System.Windows.Forms

Public Module Program
    <STAThread>
    Public Function Main(arguments As String()) As Integer
        Dim context As InstallContext = Nothing
        Try
            If arguments.Contains("--discover") Then
                ' Setup asks for the detected game folder before choosing its destination.
                SafeFiles.WriteAtomic(Argument(arguments, "--discover", Nothing), Text.Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Discovery.GamePath())))
                Return 0
            End If
            Dim root = Argument(arguments, "--game", Discovery.GamePath())
            Dim worker = arguments.Contains("--recover-worker") OrElse arguments.Contains("--apply-worker")
            If arguments.Contains("--recover-worker") AndAlso arguments.Contains("--apply-worker") Then Throw New IOException("Choose one file-worker operation.")
            Dim ownerBase = If(worker, Argument(arguments, "--owner-base", Nothing), Nothing)
            If worker AndAlso (String.IsNullOrWhiteSpace(ownerBase) OrElse Not Path.IsPathFullyQualified(ownerBase)) Then Throw New IOException("The recovery worker requires an absolute owner data folder.")
            Dim graphics = If(worker, Argument(arguments, "--graphics", Nothing), Nothing)
            If worker Then RecoveryWorker.ValidateGraphicsPath(graphics)
            context = New InstallContext(root, ownerBase, graphics)
            If worker Then
                If arguments.Contains("--apply-worker") Then
                    Call (New OwnedFileTransaction(context)).ApplyPrepared()
                Else
                    Call (New OwnedFileTransaction(context)).Recover(RecoveryWorker.ParseChoice(Argument(arguments, "--graphics-choice", "ask")))
                End If
                Return 0
            End If
            If arguments.Contains("--check-install") OrElse arguments.Contains("--check-uninstall") Then
                ' Setup and uninstall must not replace or remove the launcher mid-session or mid-recovery.
                If arguments.Contains("--check-install") Then context.ValidateGame()
                If InstallContext.GameRunning() Then Throw New IOException("Close DiRT 3 and let DiRT3VR restore its files first.")
                If New OwnedFileTransaction(context).Pending Then Throw New IOException("File recovery is pending for this game folder. Open DiRT3VR and use Restore original files first.")
                If arguments.Contains("--check-install") Then Console.WriteLine("Supported DiRT 3 Complete Edition executable. Experimental VDXR requires an explicit launcher opt-in and a matching package; full interactive acceptance is pending.")
                Return 0
            End If
            If arguments.Contains("--probe-runtime") Then
                Dim settings = LauncherSettings.Load(context)
                settings.RuntimeMode = Argument(arguments, "--runtime", settings.RuntimeMode)
                settings.Validate()
                If settings.RuntimeMode = "steamvr" Then
                    settings.SteamRuntimeManifest = Argument(arguments, "--manifest", settings.SteamRuntimeManifest)
                ElseIf settings.RuntimeMode = "other" Then
                    settings.OtherRuntimeManifest = Argument(arguments, "--manifest", settings.OtherRuntimeManifest)
                Else
                    settings.VdxrRuntimeManifest = Argument(arguments, "--manifest", settings.VdxrRuntimeManifest)
                End If
                Console.WriteLine(VrRuntime.ProbeAsync(context, settings).GetAwaiter().GetResult())
                Return 0
            End If
            Application.SetCompatibleTextRenderingDefault(False)
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)
            Application.SetColorMode(SystemColorMode.System)
            Application.EnableVisualStyles()
            ' One launcher per Windows sign-in: starting it again shows the running window, also
            ' while it waits in the notification area for DiRT 3 to close.
            Using instance As New SingleInstance("DiRT3VR")
                If Not instance.Primary Then
                    instance.SignalPrimary()
                    Return 0
                End If
                Return RunLauncher(context, instance)
            End Using
        Catch ex As Exception
            Dim errorLog = Array.IndexOf(arguments, "--error-log")
            If errorLog >= 0 AndAlso errorLog + 1 < arguments.Length Then
                Try
                    SafeFiles.WriteAtomic(arguments(errorLog + 1), Text.Encoding.UTF8.GetBytes(ex.Message))
                Catch
                    ' The nonzero exit code still reports the failure to Setup.
                End Try
            End If
            If arguments.Contains("--recover-worker") OrElse arguments.Contains("--apply-worker") Then
                If context IsNot Nothing Then
                    Try
                        SafeFiles.SaveJson(Path.Combine(context.UserRoot, "logs", "recovery-worker.json"), New With {.updatedUtc = DateTime.UtcNow, .message = ex.Message, .errorType = ex.GetType().FullName})
                    Catch
                        ' The original nonzero exit remains available when even logging is denied.
                    End Try
                End If
            ElseIf arguments.Any(Function(a) a.StartsWith("--check", StringComparison.Ordinal) OrElse a = "--probe-runtime" OrElse a = "--discover") Then
                Console.Error.WriteLine(ex.Message)
            Else
                MessageBox.Show(ex.Message, "DiRT3VR", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End If
            Return 1
        End Try
    End Function

    Private Function RunLauncher(context As InstallContext, instance As SingleInstance) As Integer
        ' Setup and uninstall detect a running launcher through this name.
        Dim running As System.Threading.Mutex = Nothing
        Try
            running = New System.Threading.Mutex(False, "Global\DiRT3VR.Session")
        Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Threading.WaitHandleCannotBeOpenedException
            ' Installer detection is optional; the launcher does not depend on it.
        End Try
        Try
            Using mainWindow As New MainForm(context)
                instance.Listen(Sub()
                                    Try
                                        If mainWindow.IsHandleCreated AndAlso Not mainWindow.IsDisposed Then mainWindow.BeginInvoke(New Action(AddressOf mainWindow.ShowFromBackground))
                                    Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ObjectDisposedException
                                        ' The launcher is closing.
                                    End Try
                                End Sub)
                Application.Run(mainWindow)
            End Using
        Finally
            running?.Dispose()
        End Try
        Return 0
    End Function

    Public Function Argument(args As String(), key As String, fallback As String) As String
        Dim index = Array.IndexOf(args, key)
        If index < 0 Then Return fallback
        If index + 1 >= args.Length OrElse args(index + 1).StartsWith("--", StringComparison.Ordinal) Then Throw New ArgumentException("Missing value for " & key)
        Return args(index + 1)
    End Function
End Module
