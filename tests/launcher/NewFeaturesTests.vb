Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Module NewFeaturesTests
    ' Features of every kind, first shipped in the next version: the drawing checks use these, so they don't
    ' depend on what the next release happens to add.
    Private ReadOnly Kinds As New Dictionary(Of String, String) From {
        {"SectionLaunchmode", NewFeatures.NextVersion}, {"RecenterKey", NewFeatures.NextVersion}, {"RecenterKeepLevel", NewFeatures.NextVersion},
        {"LiveInput", NewFeatures.NextVersion}, {"LauncherView", NewFeatures.NextVersion}}

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        check(NewFeatures.Shown("0.1.20", False, "0.1.21"), "a development build before the release shows the marks")
        check(NewFeatures.Shown("0.1.21-beta.1", True, "0.1.21") AndAlso NewFeatures.Shown("0.1.21-experimental.2+abc", True, "0.1.21"), "the release's experimental builds show them")
        check(Not NewFeatures.Shown("0.1.21", False, "0.1.21"), "a stable release never shows them")
        check(Not NewFeatures.Shown("0.1.22-beta.1", True, "0.1.21") AndAlso Not NewFeatures.Shown("0.1.22", False, "0.1.21") AndAlso Not NewFeatures.Shown("0.2.0", True, "0.1.21"), "the next version drops them")
        check(Not NewFeatures.Shown("garbage", True, "0.1.21") AndAlso Not NewFeatures.Shown(Nothing, True, "0.1.21"), "an unreadable version shows nothing")
        check(NewFeatures.NamesFor(NewFeatures.NextVersion, False).Count = 0 AndAlso NewFeatures.NamesFor(NewFeatures.NextVersion & "-beta.1", True).Count = NewFeatures.Listed.Count,
              "a stable build marks nothing; the experimental build marks every listed feature")
        ' After a release, NextVersion must move on (and the released entries go): this build, or the release's
        ' Experimental-ring package of it (plain version, package.ps1 -Channel Experimental), must still show every mark.
        check(NewFeatures.NamesFor(BuildInfo.Version, True).Count = NewFeatures.Listed.Count,
              "this build (" & BuildInfo.Version & ") is before " & NewFeatures.NextVersion & " or its experimental build; update NewFeatures after a release")
    End Sub

    Private Function AccentPixels(image As Bitmap, Optional fromX As Integer = 0) As Integer
        Dim accent = NewFeatures.Color, count = 0
        For x = fromX To image.Width - 1
            For y = 0 To image.Height - 1
                Dim pixel = image.GetPixel(x, y)
                If Math.Abs(CInt(pixel.R) - accent.R) + Math.Abs(CInt(pixel.G) - accent.G) + Math.Abs(CInt(pixel.B) - accent.B) < 60 Then count += 1
            Next
        Next
        Return count
    End Function

    Private Function Snapshot(control As Control) As Bitmap
        Dim image As New Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height))
        control.DrawToBitmap(image, New Rectangle(Point.Empty, image.Size))
        Return image
    End Function

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "new-marks")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "new-marks-users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        ' The launcher as the release's Experimental-ring package runs it; test builds are Stable.
        Dim experimentalBuild = NewFeatures.ExperimentalBuild
        NewFeatures.ExperimentalBuild = True
        Try
            RunMarkedForm(ctx, check)
        Finally
            NewFeatures.ExperimentalBuild = experimentalBuild
        End Try

        ' A stable build marks nothing.
        Using panel As New Panel()
            Dim header As New SectionHeader("Launch mode")
            panel.Controls.Add(header)
            check(NewFeatures.Apply(panel, NewFeatures.NextVersion, False, Kinds).Count = 0 AndAlso Not header.IsNew, "a stable release leaves every control unmarked")
            check(NewFeatures.Apply(panel, NewFeatures.NextVersion & "-beta.1", True, Kinds).Count = 1 AndAlso header.IsNew, "its experimental build marks them")
        End Using
    End Sub

    Private Sub RunMarkedForm(ctx As InstallContext, check As Action(Of Boolean, String))
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim headerOf = Function(title As String) form.SectionHeaders.Single(Function(header) header.Title = title)
            Dim missing = NewFeatures.Listed.Where(Function(name) form.Controls.Find(name, True).Length = 0 AndAlso Not form.SectionHeaders.Any(Function(header) header.Name = name)).ToArray()
            check(missing.Length = 0, "every listed feature exists in the launcher (" & String.Join(", ", missing) & ")")
            Dim rowOf = Function(name As String) As SettingRow
                            Dim item As Control = form.Controls.Find(name, True).Single()
                            While item IsNot Nothing AndAlso Not TypeOf item Is SettingRow
                                item = item.Parent
                            End While
                            Return DirectCast(item, SettingRow)
                        End Function
            check(Not rowOf("SeatMenuKey").IsNew AndAlso Not rowOf("DesktopAnisotropy").IsNew AndAlso Not rowOf("RecenterKey").IsNew AndAlso Not rowOf("VrRenderScale").IsNew AndAlso Not headerOf("Launch mode").IsNew,
                  "features of earlier releases, 0.1.22's included, don't carry the mark")

            ' Each kind of mark, with features of every kind.
            check(NewFeatures.Apply(form, NewFeatures.NextVersion & "-beta.1", True, Kinds).Count = Kinds.Count, "every kind of feature is marked")
            check(headerOf("Launch mode").IsNew AndAlso Not headerOf("Startup").IsNew AndAlso Not headerOf("VR rendering").IsNew, "a new section carries the mark; older sections don't")
            check(rowOf("RecenterKey").IsNew AndAlso Not rowOf("RuntimeChoice").IsNew, "a new settings row carries the mark on its label; older rows don't")
            Dim keepLevel = form.Controls.Find("RecenterKeepLevel", True).Single()
            check(NewFeatures.IsMarked(keepLevel) AndAlso NewFeatures.IsMarked(form.Controls.Find("LiveInput", True).Single()) AndAlso Not NewFeatures.IsMarked(form.Controls.Find("SkipStartupMovies", True).Single()),
                  "new buttons and options are marked beside them; older ones aren't")
            check(DirectCast(form.Controls.Find("LauncherView", True).Single(), LauncherViewPicker).IsNew, "the Simple | Advanced switch is marked")

            ' The mark is drawn in the accent colour after the title, and only there.
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            tabs.SelectedIndex = 0 : Application.DoEvents()
            Dim launchMode = headerOf("Launch mode"), startup = headerOf("Startup")
            Dim titleWidth = TextRenderer.MeasureText(launchMode.Text, launchMode.Font, Size.Empty, TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix).Width
            Using marked = Snapshot(launchMode), plain = Snapshot(startup)
                check(launchMode.Width >= titleWidth + NewFeatures.Width(launchMode.Font) AndAlso AccentPixels(marked, titleWidth) > 10 AndAlso AccentPixels(plain) = 0,
                      "the heading's " & NewFeatures.Mark & " is drawn in the accent colour after the title (" & AccentPixels(marked, titleWidth) & " accent pixels)")
            End Using
            Dim row = rowOf("RecenterKey")
            tabs.SelectedIndex = 2 : Application.DoEvents()
            check(keepLevel.Width <= keepLevel.PreferredSize.Width + 1 AndAlso keepLevel.Parent.Width - keepLevel.Right >= NewFeatures.Width(keepLevel.Font), "a marked option keeps its own width, leaving room for the mark")
            Dim caption = row.Controls.Find("RecenterKeyLabel", False).Single()
            Using marked = Snapshot(caption)
                check(AccentPixels(marked) > 10, "the row label's mark is drawn in the accent colour")
            End Using
            Using marked = Snapshot(keepLevel.Parent)
                check(AccentPixels(marked, keepLevel.Right) > 10, "the option's mark is drawn beside it")
            End Using
            form.Close()
        End Using
    End Sub
End Module
