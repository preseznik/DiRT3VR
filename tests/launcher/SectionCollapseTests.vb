Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Module SectionCollapseTests
    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' A heading hides only the rows up to the next heading and restores the exact layout.
        Using stack As New VerticalStack With {.Width = 400, .Visible = True}
            Dim first As New SectionHeader("First"), second As New SectionHeader("Second")
            Dim a As New Label With {.Text = "a", .AutoSize = True}, b As New CheckBox With {.Text = "b"}, c As New Label With {.Text = "c", .AutoSize = True}
            stack.Controls.AddRange(New Control() {first, a, b, second, c}) : stack.PerformLayout()
            Dim height = stack.Height, secondTop = second.Top, bBounds = b.Bounds
            check(Not first.Collapsed AndAlso first.Text = "▾ First" AndAlso first.AccessibleName = "First" AndAlso a.Visible AndAlso b.Visible AndAlso first.TabStop,
                  "sections start open and focusable")
            first.PerformClick()
            check(first.Collapsed AndAlso first.Text = "▸ First" AndAlso first.AccessibleDescription.StartsWith("Collapsed"), "a click collapses the heading")
            check(Not a.Visible AndAlso Not b.Visible, "collapsing hides only its own rows")
            check(second.Visible AndAlso c.Visible AndAlso second.Top < secondTop AndAlso stack.Height < height, "the next section moves up")
            first.PerformClick()
            check(Not first.Collapsed AndAlso a.Visible AndAlso b.Visible AndAlso b.Bounds = bBounds AndAlso second.Top = secondTop AndAlso stack.Height = height,
                  "expanding restores the original layout")
            first.Collapsed = True : stack.Controls.Remove(a)
            check(a.Visible, "a row moved out of a collapsed section becomes visible again")
            first.Collapsed = False : second.Collapsed = True
            check(b.Visible AndAlso Not c.Visible, "sections collapse independently")
            a.Dispose()
        End Using

        ' The launcher's headings and their remembered state.
        Dim game = Path.Combine(root, "sections")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "section-users"), Path.Combine(game, "graphics.xml"), root)
        Using form As New MainForm(ctx)
            Dim titles = form.SectionHeaders.Select(Function(h) h.Title).ToHashSet()
            For Each expected In {"Launch mode", "Startup", "Desktop", "VR rendering", "VR and seat controls", "Appearance", "Settings profiles", "VR runtime", "Diagnostics"}
                check(titles.Contains(expected), "launcher has the collapsible heading " & expected)
            Next
            check(form.SectionHeaders.All(Function(h) Not h.Collapsed AndAlso Object.Equals(h.Tag, "section")) AndAlso form.CollapsedSections Is Nothing, "all launcher sections start open")
            Dim rendering = form.SectionHeaders.First(Function(h) h.Title = "VR rendering")
            Dim desktop = form.SectionHeaders.First(Function(h) h.Title = "Desktop")
            Dim section = rendering.Parent
            section.PerformLayout()
            Dim openHeight = section.Height
            rendering.Collapsed = True
            check(section.Height < openHeight AndAlso section.Height <= rendering.Bottom + rendering.Margin.Bottom + section.Padding.Bottom,
                  "a collapsed launcher section shrinks to its heading")
            check(form.CollapsedSections = "VR rendering", "collapsing is remembered")
            desktop.Collapsed = True
            check(form.CollapsedSections = "Desktop|VR rendering", "the state lists every collapsed title")
            desktop.Collapsed = False
            check(form.CollapsedSections = "VR rendering", "expanding forgets the title")
            check(Not form.HasUnsavedSettings(), "collapsing sections is not a settings change")
            form.ApplyCollapsedSections("Startup|No such section")
            check(Not rendering.Collapsed AndAlso form.SectionHeaders.First(Function(h) h.Title = "Startup").Collapsed AndAlso form.CollapsedSections = "Startup",
                  "saved state is restored and unknown titles are ignored")
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            ' Headings read as text: no button border in either theme, including Windows dark mode.
            Dim launchMode = form.SectionHeaders.First(Function(h) h.Title = "Launch mode")
            Using image As New Bitmap(launchMode.Width, launchMode.Height)
                launchMode.DrawToBitmap(image, New Rectangle(Point.Empty, image.Size))
                Dim back = launchMode.BackColor.ToArgb()
                check({image.GetPixel(0, 0), image.GetPixel(image.Width - 1, image.Height \ 2), image.GetPixel(image.Width \ 2, image.Height - 1)}.All(Function(p) p.ToArgb() = back),
                      "section headings are drawn without a border")
            End Using
            ' New sections must stay collapsible: a section is a stack that starts with a
            ' registered SectionHeader from LauncherLayout.Section, never a plain bold label.
            Dim tabs = form.Controls.Find("LauncherTabs", True).Single()
            Dim plainHeadings = Descendants(tabs).OfType(Of VerticalStack)().Where(Function(s) s.Controls.Count > 0).Select(Function(s) s.Controls(0)).OfType(Of Label)().
                Where(Function(l) l.Font.Bold AndAlso l.Font.SizeInPoints >= 11).Select(Function(l) l.Text).ToArray()
            check(plainHeadings.Length = 0, "launcher sections use collapsible headings: " & String.Join(", ", plainHeadings))
            check(Descendants(tabs).OfType(Of SectionHeader)().Count() = form.SectionHeaders.Count, "every launcher heading remembers its collapsed state")
            form.Close()
        End Using
        check(WindowPreferences.Load(ctx).CollapsedSections = "Startup", "collapsed sections are saved with the window size on close")
        Using reopened As New MainForm(ctx)
            check(reopened.SectionHeaders.Single(Function(h) h.Title = "Startup").Collapsed AndAlso reopened.SectionHeaders.Where(Function(h) h.Collapsed).Count() = 1,
                  "a reopened launcher restores collapsed sections")
        End Using
    End Sub

    Private Iterator Function Descendants(parent As Control) As IEnumerable(Of Control)
        For Each child As Control In parent.Controls
            Yield child
            For Each nested In Descendants(child)
                Yield nested
            Next
        Next
    End Function
End Module
