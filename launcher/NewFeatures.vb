Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

' "*new*" marks on launcher features (iztok's #de0e3638, for every VR launcher), drawn in the launcher's accent
' colour: yellow for DiRT3VR. Every build marks the features added since the most recent Stable release before it
' (iztok, #7cde1cab): a Stable release shows everything new since the previous Stable, and each Experimental after it
' shows what's new since that Stable, so a mark stays until the first Experimental after the next Stable.
Public NotInheritable Class NewFeatures
    Public Const Mark As String = "*new*"
    ' The most recent Stable release. After a Stable release, set it to that version and remove the entries up to it.
    Public Const LastStable As String = "0.1.23"
    ' The next release. After every release, set the version after it; entries added since use it
    ' (NewFeaturesTests fails while this build is past NextVersion).
    Public Const NextVersion As String = "0.1.31"

    ' Control or section names, and the version that first ships them: each launcher setting, section, option or
    ' button added since LastStable, with NextVersion at the time it was added. Empty right after a Stable release.
    Private Shared ReadOnly Since As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"DesktopBloom", "0.1.24"}, {"DesktopGlare", "0.1.24"}, {"VrBloom", "0.1.24"}, {"VrGlare", "0.1.24"},
        {"DesktopColourFilter", "0.1.24"}, {"VrColourFilter", "0.1.24"}, {"CockpitSteeringShakeOff", "0.1.26"},
        {"LauncherView", "0.1.27"}, {"DesktopDepthBlur", "0.1.28"}, {"VrDepthBlur", "0.1.28"}, {"DirectConditions", "0.1.29"}}
    Private Shared ReadOnly marked As New ConditionalWeakTable(Of Control, Object)

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property Listed As IReadOnlyCollection(Of String)
        Get
            Return Since.Keys
        End Get
    End Property

    ' Whether a feature first shipped in firstVersion is marked: it came after the most recent Stable release
    ' (stable, LastStable by default). An unreadable version is never marked.
    Public Shared Function Shown(firstVersion As String, Optional stable As String = Nothing) As Boolean
        Dim first As Version = Nothing, last As Version = Nothing
        If Not Version.TryParse(If(firstVersion, ""), first) OrElse Not Version.TryParse(If(stable, LastStable), last) Then Return False
        Return first > last
    End Function

    ' features: the names and first versions to consider instead of the listed ones (for the tests).
    Public Shared Function NamesFor(Optional stable As String = Nothing, Optional features As IReadOnlyDictionary(Of String, String) = Nothing) As IReadOnlyList(Of String)
        Return If(features, Since).Where(Function(entry) Shown(entry.Value, stable)).Select(Function(entry) entry.Key).ToArray()
    End Function

    ' Marks the listed controls under root; returns the names marked.
    Public Shared Function Apply(root As Control, Optional stable As String = Nothing, Optional features As IReadOnlyDictionary(Of String, String) = Nothing) As IReadOnlyList(Of String)
        Dim names = NamesFor(stable, features).ToHashSet(StringComparer.Ordinal)
        Dim done As New List(Of String)
        For Each item In Descendants(root).ToArray()
            If names.Contains(item.Name) Then MarkControl(item) : done.Add(item.Name)
        Next
        Return done
    End Function

    Private Shared Iterator Function Descendants(parent As Control) As IEnumerable(Of Control)
        For Each child As Control In parent.Controls
            Yield child
            For Each nested In Descendants(child)
                Yield nested
            Next
        Next
    End Function

    ' Headings and the view switch draw their own mark; a settings row marks its label; anything else
    ' is marked by the stack it sits in, to its right.
    Private Shared Sub MarkControl(item As Control)
        If TypeOf item Is SectionHeader Then
            DirectCast(item, SectionHeader).IsNew = True
            Return
        End If
        If TypeOf item Is LauncherViewPicker Then
            DirectCast(item, LauncherViewPicker).IsNew = True
            Return
        End If
        Dim parent = item.Parent
        While parent IsNot Nothing AndAlso Not TypeOf parent Is VerticalStack
            If TypeOf parent Is SettingRow Then
                DirectCast(parent, SettingRow).IsNew = True
                Return
            End If
            parent = parent.Parent
        End While
        marked.AddOrUpdate(item, Nothing)
        item.Parent?.PerformLayout() : item.Parent?.Invalidate()
    End Sub

    ' Controls a stack marks beside them.
    Public Shared Function IsMarked(item As Control) As Boolean
        Dim value As Object = Nothing
        Return item IsNot Nothing AndAlso marked.TryGetValue(item, value)
    End Function

    Public Shared ReadOnly Property Color As Color
        Get
            Return If(SystemInformation.HighContrast, SystemColors.HotTrack, LauncherAppearance.Accent)
        End Get
    End Property

    Public Shared Function Gap(owner As Control) As Integer
        Return Px(owner, 6)
    End Function

    Public Shared Function Width(font As Font) As Integer
        Return TextRenderer.MeasureText(Mark, font, Size.Empty, TextFormatFlags.NoPrefix Or TextFormatFlags.SingleLine).Width
    End Function

    Public Shared Sub Draw(graphics As Graphics, font As Font, left As Integer, top As Integer, height As Integer)
        TextRenderer.DrawText(graphics, Mark, font, New Rectangle(left, top, Width(font) + 2, height), Color,
                              TextFormatFlags.Left Or TextFormatFlags.VerticalCenter Or TextFormatFlags.NoPrefix Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPadding)
    End Sub
End Class
