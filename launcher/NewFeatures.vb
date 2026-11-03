Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

' "*new*" marks on launcher features added since the last release (iztok's #de0e3638, for every VR
' launcher), drawn in the launcher's accent colour: yellow for DiRT3VR. A mark shows in development
' builds before the release that first ships the feature and in that release's experimental builds;
' stable releases never show marks, and the next version drops them.
Public NotInheritable Class NewFeatures
    Public Const Mark As String = "*new*"
    ' The release after 0.1.21. After each release, set the next version and remove the released entries
    ' (NewFeaturesTests fails while this build is past NextVersion).
    Public Const NextVersion As String = "0.1.22"

    ' Control or section names, and the version that first ships them.
    Private Shared ReadOnly Since As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"SeatMenuKey", NextVersion}, {"SeatMenuButton", NextVersion}}
    Private Shared ReadOnly marked As New ConditionalWeakTable(Of Control, Object)
    ' Whether the running build is an experimental one (BuildInfo.Experimental); tests may set it.
    Public Shared Property ExperimentalBuild As Boolean = BuildInfo.Experimental

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property Listed As IReadOnlyCollection(Of String)
        Get
            Return Since.Keys
        End Get
    End Property

    ' Whether a feature first shipped in firstVersion is marked in a build of buildVersion.
    Public Shared Function Shown(buildVersion As String, experimental As Boolean, firstVersion As String) As Boolean
        Dim build As Version = Nothing, first As Version = Nothing
        If buildVersion Is Nothing OrElse Not Version.TryParse(buildVersion.Split("-"c, "+"c)(0), build) OrElse Not Version.TryParse(firstVersion, first) Then Return False
        Dim order = build.CompareTo(first)
        Return order < 0 OrElse (order = 0 AndAlso experimental)
    End Function

    ' features: the names and first versions to consider instead of the listed ones (for the tests).
    Public Shared Function NamesFor(buildVersion As String, experimental As Boolean, Optional features As IReadOnlyDictionary(Of String, String) = Nothing) As IReadOnlyList(Of String)
        Return If(features, Since).Where(Function(entry) Shown(buildVersion, experimental, entry.Value)).Select(Function(entry) entry.Key).ToArray()
    End Function

    ' Marks the listed controls under root for this build; returns the names marked.
    Public Shared Function Apply(root As Control, Optional buildVersion As String = Nothing, Optional experimental As Boolean? = Nothing, Optional features As IReadOnlyDictionary(Of String, String) = Nothing) As IReadOnlyList(Of String)
        Dim names = NamesFor(If(buildVersion, BuildInfo.Version), experimental.GetValueOrDefault(ExperimentalBuild), features).ToHashSet(StringComparer.Ordinal)
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
