Imports System.Globalization
Imports System.Text.RegularExpressions

' Motion blur off for VR sessions (#30d34783; Lens's tools/motion_blur.py and
' docs/research/motion-blur.md, as DiRT2VR does). In VR every head movement is camera movement, so
' DiRT 3's motion blur smears whatever is nearest; a low pre-race camera turns the nearby ground into
' streaks. postprocess/effects.xml is Ego binary XML (SeatCameras): PostProcessEffects > Effect >
' ParameterGroup name=MotionBlur > Param name=blurLength / frozenBlurLength, each value being the
' element's text. Disable sets both to 0.000000, keeping each value's surrounding whitespace; new values
' are appended to the string table and only those elements point at them, so every other byte stays.
Public NotInheritable Class MotionBlur
    Public Const Target As String = "effects"
    Private Const Zero As String = "0.000000"
    Private Shared ReadOnly Lengths As String() = {"blurLength", "frozenBlurLength"}
    Private Shared ReadOnly Value As New Regex("\A(\s*)(-?\d+(?:\.\d+)?)(\s*)\z", RegexOptions.CultureInvariant)

    Private Sub New()
    End Sub

    Public Shared Function EffectsPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, "postprocess", "effects.xml")
    End Function

    Private Shared Function Named(document As SeatCameras.BinXml, element As Integer, tag As String) As IEnumerable(Of (Element As Integer, Name As String))
        Return document.Children(element).Where(Function(child) document.Name(child) = tag).
            Select(Function(child)
                       Dim name As String = Nothing
                       Return (child, If(document.AttributesOf(child).TryGetValue("name", name), name, ""))
                   End Function).ToList()
    End Function

    ' Every motion blur length element, in file order.
    Private Shared Function LengthElements(document As SeatCameras.BinXml) As List(Of Integer)
        If document.Elements.Count = 0 OrElse document.Name(0) <> "PostProcessEffects" Then Throw New IOException("Not a post-processing effects file.")
        Dim found As New List(Of (Element As Integer, Name As String))
        For Each effect In Named(document, 0, "Effect")
            For Each group In Named(document, effect.Element, "ParameterGroup").Where(Function(item) item.Name = "MotionBlur")
                found.AddRange(Named(document, group.Element, "Param").Where(Function(item) Lengths.Contains(item.Name)))
            Next
        Next
        If Not found.Any(Function(item) item.Name = "blurLength") Then Throw New IOException("No motion blur lengths in this effects file.")
        Return found.Select(Function(item) item.Element).ToList()
    End Function

    Private Shared Function Text(document As SeatCameras.BinXml, element As Integer) As String
        Dim index = document.Elements(element)(1)
        Return If(index > 0, document.Text(index), "")
    End Function

    Private Shared Function Number(text As String) As Double
        Dim match = Value.Match(text)
        Dim result As Double
        If Not match.Success OrElse Not Double.TryParse(match.Groups(2).Value, NumberStyles.Float, CultureInfo.InvariantCulture, result) OrElse Not Double.IsFinite(result) Then Throw New IOException("Unexpected motion blur value.")
        Return result
    End Function

    ' The motion blur lengths of an effects file, in file order.
    Public Shared Function Read(data As Byte()) As IReadOnlyList(Of Double)
        Dim document = SeatCameras.Decode(data)
        Return LengthElements(document).Select(Function(element) Number(Text(document, element))).ToArray()
    End Function

    ' Every MotionBlur blurLength and frozenBlurLength set to zero; applying it twice gives the same bytes.
    Public Shared Function Disable(data As Byte()) As Byte()
        Dim document = SeatCameras.Decode(data)
        For Each element In LengthElements(document)
            Dim current = Text(document, element)
            If Number(current) = 0 Then Continue For
            Dim match = Value.Match(current)
            Dim replacement = match.Groups(1).Value & Zero & match.Groups(3).Value
            ' Strings are shared between elements, so a new value never edits an existing string.
            Dim index = document.Strings.IndexOf(replacement)
            If index < 0 Then
                index = document.Strings.Count
                document.Strings.Add(replacement)
            End If
            document.Elements(element)(1) = index
        Next
        Return SeatCameras.Encode(document)
    End Function

    ' The VR session's change to the effects file, or Nothing when there is nothing to change or the file
    ' isn't the expected format (then motion blur simply stays on; note says why).
    Public Shared Function SessionChange(context As InstallContext, ByRef note As String) As Byte()
        note = ""
        Dim target = EffectsPath(context)
        Try
            SafeFiles.NoLinks(target)
            If Not File.Exists(target) OrElse New FileInfo(target).Length > 1024 * 1024 Then Throw New IOException("missing")
            Dim original = File.ReadAllBytes(target)
            Dim changed = Disable(original)
            Return If(changed.SequenceEqual(original), Nothing, changed)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            note = "Motion blur stays on: DiRT 3's postprocess\effects.xml is missing or not in the expected format."
            Return Nothing
        End Try
    End Function
End Class
