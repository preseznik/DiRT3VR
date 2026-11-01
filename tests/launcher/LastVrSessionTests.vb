Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

Module LastVrSessionTests
    Private Const Enabled As String = "XR prototype enabled headset=2400x2600 render_scale=150 scene=3600x3900 game_units_per_metre=1; first tracked focused frame required"

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "last-session", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "last-session-users", name), Path.Combine(game, "graphics.xml"), root)
    End Function

    Private Sub WriteSession(ctx As InstallContext, log As String, age As TimeSpan)
        Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        File.WriteAllText(Path.Combine(folder, "native-4242.log"), log)
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow - age)
    End Sub

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim started = New DateTime(2026, 11, 1, 7, 30, 0, DateTimeKind.Utc)
        Dim good = LastVrSession.Parse("x" & vbLf & "XR display refresh rate=90.00 Hz" & vbLf & Enabled & vbLf & "XR prototype main=12 submitted=1" & vbLf, started)
        check(good IsNot Nothing AndAlso good.RefreshHz.GetValueOrDefault() = 90.0 AndAlso good.SceneWidth = 3600 AndAlso good.SceneHeight = 3900 AndAlso good.RenderScale = 150 AndAlso good.StereoFrames AndAlso good.ActualWidth = 0, "a normal session's refresh rate and scene size are read from its log")
        check(good.Describe().Contains("headset refresh 90 Hz") AndAlso good.Describe().Contains("scene 3600 × 3900 per eye at 150%, as requested"), "it reads as refresh rate and scene size (" & good.Describe() & ")")
        Dim wrong = LastVrSession.Parse(Enabled & vbLf & "XR display refresh rate=72.5 Hz" & vbLf & "XR scene resolution mismatch requested=3600x3900 actual=1280x720" & vbLf, started)
        check(wrong.ActualWidth = 1280 AndAlso wrong.ActualHeight = 720 AndAlso wrong.Describe().Contains("DiRT 3 rendered 1280 × 720 instead of the requested 3600 × 3900") AndAlso wrong.Describe().Contains("72.5 Hz"), "a scene size DiRT 3 replaced is shown against the requested one")
        Dim unavailable = LastVrSession.Parse("XR display refresh rate unavailable: runtime lacks XR_FB_display_refresh_rate" & vbLf & Enabled, started)
        check(unavailable.RefreshHz.HasValue AndAlso Double.IsNaN(unavailable.RefreshHz.Value) AndAlso unavailable.Describe().Contains("refresh rate not reported by the runtime") AndAlso unavailable.Describe().Contains("actual size is unknown"), "a runtime without the refresh-rate extension and a session without stereo frames say so")
        Dim older = LastVrSession.Parse(Enabled & vbLf & "XR prototype main=3 submitted=1", started)
        check(Not older.RefreshHz.HasValue AndAlso Not older.Describe().Contains("refresh"), "logs from builds before the refresh-rate line show only the scene size")
        check(LastVrSession.Parse("XR prototype stopped reason=missing-stop-path", started) Is Nothing, "a session that never started rendering shows nothing")

        Dim ctx = Context(root, "load")
        check(LastVrSession.Load(ctx) Is Nothing, "no logs: nothing to show")
        WriteSession(ctx, Enabled & vbLf & "XR display refresh rate=80.00 Hz" & vbLf & "XR prototype main=1 submitted=1", TimeSpan.FromDays(3))
        WriteSession(ctx, Enabled & vbLf & "XR display refresh rate=120.00 Hz" & vbLf & "XR prototype main=1 submitted=1", TimeSpan.FromHours(1))
        Directory.CreateDirectory(Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))) ' Newest, but no native log.
        Dim loaded = LastVrSession.Load(ctx)
        check(loaded IsNot Nothing AndAlso loaded.RefreshHz.GetValueOrDefault() = 120.0, "the newest session with a native log is shown")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim summary = form.Controls.Find("LastVrSession", True).Single()
            check(summary.Text.StartsWith("No VR session", StringComparison.Ordinal), "before any VR session the Graphics tab says what will appear")
            form.Close()
        End Using
        WriteSession(ctx, Enabled & vbLf & "XR display refresh rate=90.00 Hz" & vbLf & "XR prototype main=1 submitted=1", TimeSpan.FromMinutes(5))
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim summary = form.Controls.Find("LastVrSession", True).Single()
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            check(summary.Text.StartsWith("Last VR session", StringComparison.Ordinal) AndAlso summary.Text.Contains("90 Hz") AndAlso tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Text = "Graphics").Contains(summary),
                  "Graphics shows the last session's refresh rate and scene size (" & summary.Text & ")")
            form.Close()
        End Using
    End Sub
End Module
