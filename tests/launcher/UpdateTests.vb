Imports DiRT3VR
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Threading
Imports System.Windows.Forms

Module UpdateTests
    ' Answers GitHub requests from a table; unknown URLs fail the test instead of reaching the network.
    Private NotInheritable Class FakeGitHub
        Inherits HttpMessageHandler
        Public ReadOnly Responses As New Dictionary(Of String, Func(Of HttpResponseMessage))(StringComparer.Ordinal)
        Public ReadOnly Requests As New List(Of String)
        Protected Overrides Function SendAsync(request As HttpRequestMessage, cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)
            Dim url = request.RequestUri.AbsoluteUri
            Requests.Add(url)
            Dim answer As Func(Of HttpResponseMessage) = Nothing
            If Not Responses.TryGetValue(url, answer) Then Throw New InvalidOperationException("Unexpected request: " & url)
            Return Task.FromResult(answer())
        End Function
    End Class

    Private Function Json(body As String, Optional status As HttpStatusCode = HttpStatusCode.OK) As Func(Of HttpResponseMessage)
        Return Function() New HttpResponseMessage(status) With {.Content = New StringContent(body)}
    End Function

    Private Function Release(tag As String, Optional prerelease As Boolean = False, Optional draft As Boolean = False, Optional digest As String = Nothing,
                             Optional size As Long = 3, Optional url As String = Nothing, Optional name As String = Nothing) As String
        Dim version = tag.TrimStart("v"c)
        name = If(name, "DiRT3VR-" & version & "-Setup.exe")
        url = If(url, UpdateService.Repository & "/releases/download/" & tag & "/" & name)
        digest = If(digest, "sha256:" & New String("a"c, 64))
        Return "{""tag_name"":""" & tag & """,""draft"":" & draft.ToString().ToLowerInvariant() & ",""prerelease"":" & prerelease.ToString().ToLowerInvariant() &
               ",""assets"":[{""name"":""" & name & """,""state"":""uploaded"",""browser_download_url"":""" & url & """,""digest"":""" & digest & """,""size"":" & size & "}]}"
    End Function

    Private Function Reject(action As Action, Optional contains As String = Nothing) As Boolean
        Try
            action()
        Catch ex As Exception When TypeOf ex Is IOException OrElse (TypeOf ex Is AggregateException AndAlso TypeOf ex.InnerException Is IOException)
            Dim message = If(TypeOf ex Is AggregateException, ex.InnerException.Message, ex.Message)
            Return contains Is Nothing OrElse message.Contains(contains)
        End Try
        Return False
    End Function

    Private Sub Pump(work As Task, Optional seconds As Integer = 20)
        Dim deadline = Environment.TickCount64 + seconds * 1000L
        While Not work.IsCompleted AndAlso Environment.TickCount64 < deadline
            Application.DoEvents() : Thread.Sleep(10)
        End While
        If Not work.IsCompleted Then Throw New TimeoutException("UI work did not finish.")
    End Sub

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "update-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' Version order follows SemVer, including numeric prerelease parts.
        Dim order = {"0.1.9", "0.1.20", "0.2.0-alpha.2", "0.2.0-alpha.10", "0.2.0-beta", "0.2.0", "v1.0.0"}
        For i = 1 To order.Length - 1
            check(ReleaseVersion.Parse(order(i - 1)).CompareTo(ReleaseVersion.Parse(order(i))) < 0, "release order " & order(i - 1) & " < " & order(i))
        Next
        check(ReleaseVersion.Parse("0.1.20+abc").CompareTo(ReleaseVersion.Parse("0.1.20")) = 0, "build metadata is not an update")
        For Each invalid In {"", "1.2", "01.2.3", "1.2.3-01", "1.2.3.4", "latest", Nothing}
            check(Reject(Sub() ReleaseVersion.Parse(invalid)), "invalid release version refused: " & If(invalid, "(null)"))
        Next
        check(Not String.IsNullOrEmpty(BuildInfo.Version) AndAlso ReleaseVersion.Parse(BuildInfo.Version) IsNot Nothing AndAlso Not BuildInfo.Version.Contains("+"c), "the launcher knows its own release version")

        Dim list = "[" & String.Join(",", {Release("v0.0.1"), Release("v99.0.0"), Release("v99.1.0-beta.1"), Release("v98.0.0", draft:=True), Release("v99.2.0", prerelease:=True), "{""broken"":true}"}) & "]"
        Dim stable = UpdateService.SelectUpdate(list, "0.1.20", False)
        check(stable IsNot Nothing AndAlso stable.Version.Text = "99.0.0" AndAlso Not stable.IsExperimental AndAlso stable.Download.EndsWith("/v99.0.0/DiRT3VR-99.0.0-Setup.exe", StringComparison.Ordinal) AndAlso stable.Digest = New String("a"c, 64) AndAlso stable.Size = 3, "newest stable release with a verified installer is offered")
        Dim preview = UpdateService.SelectUpdate(list, "0.1.20", True)
        check(preview IsNot Nothing AndAlso preview.Version.Text = "99.2.0" AndAlso preview.IsExperimental, "experimental releases are offered only when opted in")
        check(UpdateService.SelectUpdate(list, "99.0.0", False) Is Nothing, "no update when the newest stable release is installed")
        check(UpdateService.SelectStable(list).Version.Text = "99.0.0", "return to stable finds the newest stable release")
        For Each unsafe In {Release("v99.0.0", url:="https://example.com/DiRT3VR-99.0.0-Setup.exe"), Release("v99.0.0", digest:="md5:abc"), Release("v99.0.0", digest:=""),
                            Release("v99.0.0", size:=0), Release("v99.0.0", size:=600L * 1024 * 1024), Release("v99.0.0", name:="DiRT3VR-99.0.0-Setup.zip"),
                            Release("v99.0.0", url:=UpdateService.Repository & "/releases/download/v98.0.0/DiRT3VR-99.0.0-Setup.exe")}
            Dim found = UpdateService.SelectUpdate("[" & unsafe & "]", "0.1.20", False)
            check(found IsNot Nothing AndAlso found.Download Is Nothing, "a release without an exact, verified installer is listed but never downloaded")
        Next

        Using github As New FakeGitHub(), client As New HttpClient(github)
            Dim service As New UpdateService(client)
            github.Responses(UpdateService.Api) = Json("Not Found", HttpStatusCode.NotFound)
            check(Reject(Sub() service.CheckAsync("0.1.20", False, CancellationToken.None).GetAwaiter().GetResult(), UpdateService.NoPublicReleases), "a private repository reads as no public releases yet")
            github.Responses(UpdateService.Api) = Json("rate limited", HttpStatusCode.Forbidden)
            check(Reject(Sub() service.CheckAsync("0.1.20", False, CancellationToken.None).GetAwaiter().GetResult(), "limit"), "GitHub rate limits are explained")
            Dim second = "https://api.github.com/repos/preseznik/DiRT3VR/releases?per_page=100&page=2"
            github.Responses(UpdateService.Api) = Function()
                                                       Dim response As New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent("[" & Release("v99.0.0") & "]")}
                                                       response.Headers.Add("Link", "<" & second & ">; rel=""next""")
                                                       Return response
                                                   End Function
            github.Responses(second) = Json("[" & Release("v99.3.0") & "]")
            check(service.CheckAsync("0.1.20", False, CancellationToken.None).GetAwaiter().GetResult().Version.Text = "99.3.0", "release pages are followed")
            github.Responses(UpdateService.Api) = Function()
                                                       Dim response As New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent("[]")}
                                                       response.Headers.Add("Link", "<https://evil.example/repos/preseznik/DiRT3VR/releases?per_page=100&page=2>; rel=""next""")
                                                       Return response
                                                   End Function
            check(Reject(Sub() service.CheckAsync("0.1.20", False, CancellationToken.None).GetAwaiter().GetResult(), "pagination"), "pagination never leaves api.github.com")

            Dim payload = Text.Encoding.ASCII.GetBytes("setup-bytes")
            Dim good As New ReleaseUpdate With {.Version = ReleaseVersion.Parse("99.0.0"), .Download = UpdateService.Repository & "/releases/download/v99.0.0/DiRT3VR-99.0.0-Setup.exe",
                                                .Digest = Convert.ToHexString(SHA256.HashData(payload)), .Size = payload.Length, .AssetName = "DiRT3VR-99.0.0-Setup.exe"}
            github.Responses(good.Download) = Function() New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New ByteArrayContent(payload)}
            Dim folder = Path.Combine(root, "update-downloads")
            Dim percents As New List(Of Integer)
            Dim saved = service.DownloadAsync(good, folder, New SyncProgress(percents), CancellationToken.None).GetAwaiter().GetResult()
            check(File.ReadAllBytes(saved).SequenceEqual(payload) AndAlso saved.EndsWith("-DiRT3VR-99.0.0-Setup.exe", StringComparison.Ordinal) AndAlso percents.Contains(100), "a verified installer is downloaded with progress")
            Dim wrong As New ReleaseUpdate With {.Version = good.Version, .Download = good.Download, .Digest = New String("0"c, 64), .Size = good.Size, .AssetName = good.AssetName}
            check(Reject(Sub() service.DownloadAsync(wrong, folder, Nothing, CancellationToken.None).GetAwaiter().GetResult(), "checksum"), "a checksum mismatch discards the download")
            Dim larger As New ReleaseUpdate With {.Version = good.Version, .Download = good.Download, .Digest = good.Digest, .Size = 4, .AssetName = good.AssetName}
            check(Reject(Sub() service.DownloadAsync(larger, folder, Nothing, CancellationToken.None).GetAwaiter().GetResult(), "larger"), "a download larger than GitHub reported is discarded")
            check(Directory.GetFiles(folder).Length = 0, "failed downloads leave nothing, and older installers are removed before a new download")
            check(Reject(Sub() service.DownloadAsync(New ReleaseUpdate With {.Version = good.Version}, folder, Nothing, CancellationToken.None).GetAwaiter().GetResult(), "verifiable"), "a release without an installer is never downloaded")
        End Using

        Dim ctx = Context(root, "update-preferences")
        check(Not UpdatePreferences.Load(ctx).IncludeExperimentalReleases, "experimental releases are off by default")
        UpdatePreferences.Save(ctx, True)
        check(UpdatePreferences.Load(ctx).IncludeExperimentalReleases, "the experimental choice is remembered")
        File.WriteAllText(Path.Combine(ctx.UserRoot, "update-preferences.json"), "{broken")
        check(Not UpdatePreferences.Load(ctx).IncludeExperimentalReleases, "an unreadable preference never opts into experimental releases")
        UpdatePreferences.Save(ctx, True) : Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrRenderScale = 150}.Save(ctx)
        Dim backup = UpdatePreferences.PrepareRollback(ctx)
        check(File.Exists(Path.Combine(backup, "settings.json")) AndAlso File.Exists(Path.Combine(backup, "update-preferences.json")) AndAlso Not UpdatePreferences.Load(ctx).IncludeExperimentalReleases, "return to stable backs up preferences and leaves the experimental channel")

        Dim start = UpdateService.InstallerStartInfo("C:\updates\setup.exe", "C:\Games\DiRT 3 Complete Edition")
        check(start.UseShellExecute AndAlso start.ArgumentList.SequenceEqual({"/DIR=C:\Games\DiRT 3 Complete Edition", "/NORESTART"}), "setup installs into the current game folder")
        check(Not UpdateService.IsInstalledPackage(ctx), "development and test builds don't check for updates on their own")
        Dim packaged = Path.Combine(root, "update-package")
        Directory.CreateDirectory(packaged) : File.WriteAllText(Path.Combine(packaged, "package.json"), "{}")
        check(UpdateService.IsInstalledPackage(New InstallContext(ctx.GameRoot, Path.Combine(root, "update-users"), ctx.GraphicsPath, packaged)), "installed packages check for updates")
    End Sub

    ' Progress(Of T) posts to the UI context; this records synchronously for the test.
    Private NotInheritable Class SyncProgress
        Implements IProgress(Of Integer)
        Private ReadOnly values As List(Of Integer)
        Public Sub New(target As List(Of Integer))
            values = target
        End Sub
        Public Sub Report(value As Integer) Implements IProgress(Of Integer).Report
            SyncLock values
                values.Add(value)
            End SyncLock
        End Sub
    End Class

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "update-ui")
        Dim payload = Text.Encoding.ASCII.GetBytes("setup")
        Using github As New FakeGitHub(), client As New HttpClient(github)
            Dim published = Release("v99.0.0", digest:="sha256:" & Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), size:=payload.Length)
            github.Responses(UpdateService.Api) = Json("[" & published & "]")
            github.Responses(UpdateService.Repository & "/releases/download/v99.0.0/DiRT3VR-99.0.0-Setup.exe") = Function() New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New ByteArrayContent(payload)}
            Dim started As ProcessStartInfo = Nothing
            Dim blocked = "Close DiRT 3 first."
            Dim blocker = blocked
            Using about As New AboutForm(ctx, {New HelpTopic("Instructions", "Instructions text")}, Nothing, client, Function() blocker, Sub(info) started = info)
                about.ShowInTaskbar = False : about.StartPosition = FormStartPosition.Manual : about.Location = New Drawing.Point(-30000, -30000)
                about.Show() : Application.DoEvents()
                Dim status = DirectCast(about.Controls.Find("UpdateStatus", True).Single(), Label)
                Dim install = DirectCast(about.Controls.Find("InstallUpdate", True).Single(), Button)
                check(DirectCast(about.Controls.Find("InstalledVersion", True).Single(), Label).Text.Contains(BuildInfo.Version) AndAlso DirectCast(about.Controls.Find("Instructions", True).Single(), TextBox).Text.EndsWith("Instructions text", StringComparison.Ordinal), "About shows the installed version and the instructions")
                check(Not install.Enabled, "nothing to install before a check")
                Pump(about.CheckUpdateAsync(False))
                check(status.Text.StartsWith("Stable version 99.0.0 is available", StringComparison.Ordinal) AndAlso install.Enabled, "a check finds the newer release (" & status.Text & ")")
                Pump(about.InstallUpdateAsync())
                check(status.Text = blocked AndAlso started Is Nothing AndAlso Not about.InstallerStarted, "an update waits while a DiRT 3 session is active")
                blocker = ""
                Pump(about.InstallUpdateAsync())
                check(about.InstallerStarted AndAlso started IsNot Nothing AndAlso started.ArgumentList.Contains("/DIR=" & ctx.GameRoot), "a verified download starts setup for the current game folder (" & status.Text & ")")
                check(File.ReadAllBytes(started.FileName).SequenceEqual(payload), "setup is the verified download")
                check(Not about.Visible, "the window closes once setup starts")
            End Using
            github.Responses(UpdateService.Api) = Json("Not Found", HttpStatusCode.NotFound)
            Using about As New AboutForm(ctx, {New HelpTopic("Instructions", "Text")}, Nothing, client)
                about.ShowInTaskbar = False : about.StartPosition = FormStartPosition.Manual : about.Location = New Drawing.Point(-30000, -30000)
                about.Show() : Application.DoEvents()
                Pump(about.CheckUpdateAsync(False))
                check(DirectCast(about.Controls.Find("UpdateStatus", True).Single(), Label).Text = UpdateService.NoPublicReleases, "a private repository shows a plain message")
                Dim changed = False
                AddHandler about.UpdatePreferenceChanged, Sub() changed = True
                DirectCast(about.Controls.Find("ExperimentalUpdates", True).Single(), CheckBox).Checked = True
                check(changed AndAlso UpdatePreferences.Load(ctx).IncludeExperimentalReleases, "the experimental checkbox saves the choice and tells the launcher")
                DirectCast(about.Controls.Find("ExperimentalUpdates", True).Single(), CheckBox).Checked = False
                about.Close()
            End Using
        End Using

        Dim launcherContext = Context(root, "update-notice")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(launcherContext)
        Using form As New MainForm(launcherContext)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim notice = DirectCast(form.Controls.Find("UpdateAvailable", True).Single(), Button)
            check(Not notice.Visible, "no update notice without a check")
            Pump(form.CheckForUpdateAsync(Function(token) Task.FromException(Of ReleaseUpdate)(New IOException(UpdateService.NoPublicReleases))))
            check(Not notice.Visible, "a failed startup check stays quiet")
            Pump(form.CheckForUpdateAsync(Function(token) Task.FromResult(New ReleaseUpdate With {.Version = ReleaseVersion.Parse("99.0.0-beta.1"), .IsExperimental = True})))
            check(Not notice.Visible, "experimental releases are not announced unless opted in")
            Pump(form.CheckForUpdateAsync(Function(token) Task.FromResult(New ReleaseUpdate With {.Version = ReleaseVersion.Parse("99.0.0")})))
            check(notice.Visible AndAlso notice.Text = "Update available: 99.0.0", "a newer release shows next to ?")
            form.Close()
        End Using
    End Sub
End Module
