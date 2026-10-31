Imports System.Windows.Forms

Public Module Program
    <STAThread>
    Public Function Main(arguments As String()) As Integer
        Dim context As InstallContext = Nothing
        Try
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
                    Call (New OwnedFileTransaction(context)).Recover()
                End If
                Return 0
            End If
            If arguments.Contains("--check-install") Then
                context.ValidateGame() : Console.WriteLine("Supported DiRT 3 Complete Edition executable. Experimental VDXR requires an explicit launcher opt-in and a matching package; full interactive acceptance is pending.")
                Return 0
            End If
            If arguments.Contains("--probe-runtime") Then
                Dim settings = LauncherSettings.Load(context)
                settings.RuntimeMode = Argument(arguments, "--runtime", settings.RuntimeMode)
                settings.Validate()
                If settings.RuntimeMode = "steamvr" Then
                    settings.SteamRuntimeManifest = Argument(arguments, "--manifest", settings.SteamRuntimeManifest)
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
            Application.Run(New MainForm(context))
            Return 0
        Catch ex As Exception
            If arguments.Contains("--recover-worker") OrElse arguments.Contains("--apply-worker") Then
                If context IsNot Nothing Then
                    Try
                        SafeFiles.SaveJson(Path.Combine(context.UserRoot, "logs", "recovery-worker.json"), New With {.updatedUtc = DateTime.UtcNow, .message = ex.Message, .errorType = ex.GetType().FullName})
                    Catch
                        ' The original nonzero exit remains available when even logging is denied.
                    End Try
                End If
            ElseIf arguments.Any(Function(a) a.StartsWith("--check", StringComparison.Ordinal) OrElse a = "--probe-runtime") Then
                Console.Error.WriteLine(ex.Message)
            Else
                MessageBox.Show(ex.Message, "DiRT3VR", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End If
            Return 1
        End Try
    End Function

    Public Function Argument(args As String(), key As String, fallback As String) As String
        Dim index = Array.IndexOf(args, key)
        If index < 0 Then Return fallback
        If index + 1 >= args.Length OrElse args(index + 1).StartsWith("--", StringComparison.Ordinal) Then Throw New ArgumentException("Missing value for " & key)
        Return args(index + 1)
    End Function
End Module
