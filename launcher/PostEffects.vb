Imports System.Globalization
Imports System.Text
Imports System.Text.RegularExpressions

' Bloom and light streaks + lens flares off (#3c6d6ffa; Wrench's tools/bloom.py and tools/glare.py,
' docs/research/bloom.md, as DiRT2VR's bloom option), with VR's motion blur edit (MotionBlur) on the same file.
' DiRT 3 has no setting for either: both are data in its post-processing effect files.
' - Bloom: every BloomFromDsX4 mix (mixMedium, mixLarge, mixHuge, mixStreak) set to 0 in postprocess\effects.xml
'   and tracks\generic\night_effects.xml (Ego binary XML) and tracks\generic\effects.xml (text XML, where only the
'   numbers change and the file keeps its whitespace and CRLF line endings).
' - Light streaks and lens flares: Streaks multiplier_0..2 and LensFlares multiplier set to 0 in
'   postprocess\effects.xml (the glare around the sun; each eye draws its own in VR).
' Thresholds, colours and every other group stay. Binary edits append new values to the string table and point only
' the changed elements at them, so every other byte stays as shipped; zero values keep their bytes, and applying
' an edit twice gives the same bytes. A session applies the edits in a fixed order (motion blur, then light streaks
' and lens flares, then bloom), and recovery accepts only that exact result of the recorded original.
' - Colour filter (#24f76866; Wrench's tools/colour_filter.py, docs/research/colour-filter.md): the green/cyan tint of
'   each track's colour palette, in postprocess\palettes.pssg (ColourFilter), and with it each track effect's
'   ColourBalance tintAmount set to 0 and its colour (saturation) to 1.0 in postprocess\effects.xml
'   (tools/colour_tint.py; a user's remake mod does all three, Rowan's comparison; saturation iztok's choice). Contrast,
'   tone mapping and the non-track effects stay.
' - Depth blur (#16c6c5f5; tools/depth_blur.py): every Dof blurAmount set to 0 in postprocess\effects.xml. The
'   post-processing chain always runs its depth of field pass; the cinematic cameras' effects (PreRaceDOF, DOF_far,
'   the frontend) give it a strength, race_base already has none. Focus values and every other group stay.
Public NotInheritable Class PostEffects
    Public Const MotionBlurEdit As String = "motionblur"
    Public Const GlareEdit As String = "glare"
    Public Const BloomEdit As String = "bloom"
    Public Const ColourEdit As String = "colour"
    ' The colour filter's tint and saturation in the effects file. The Colour filter switch always chooses both with
    ' ColourEdit (the palettes); a 0.1.24 journal has ColourEdit alone, one from 6afe405 ColourEdit and TintEdit.
    Public Const TintEdit As String = "tint"
    Public Const SaturationEdit As String = "saturation"
    Public Const DepthBlurEdit As String = "depthblur"
    ' The edits in the order a session applies them; ColourEdit is the only one outside the effects file. Depth blur
    ' came last (0.1.28), so earlier journals keep their order; a launcher before it refuses a journal with it.
    Public Shared ReadOnly Order As String() = {MotionBlurEdit, GlareEdit, BloomEdit, ColourEdit, TintEdit, SaturationEdit, DepthBlurEdit}
    ' Bloom's two generic track files (journal targets; postprocess\effects.xml is MotionBlur.Target).
    Public Const TrackTarget As String = "bloom-track"
    Public Const NightTarget As String = "bloom-night"
    ' The colour filter's palettes (postprocess\palettes.pssg).
    Public Const PaletteTarget As String = "colour-palettes"
    Private Const Zero As String = "0.000000"
    Private Const One As String = "1.000000"
    Private Const BloomGroup As String = "BloomFromDsX4"
    ' Track effects are named t~<country>~<track>[~<variant>]; the others are screens, menus, damage and cameras.
    Private Const TrackEffect As String = "t~"
    Private Shared ReadOnly TintParams As New Dictionary(Of String, String())(StringComparer.Ordinal) From {{"ColourBalance", {"tintAmount"}}}
    Private Shared ReadOnly SaturationParams As New Dictionary(Of String, String())(StringComparer.Ordinal) From {{"ColourBalance", {"colour"}}}
    Private Shared ReadOnly BloomMixes As String() = {"mixMedium", "mixLarge", "mixHuge", "mixStreak"}
    Private Shared ReadOnly GlareParams As New Dictionary(Of String, String())(StringComparer.Ordinal) From {
        {"Streaks", {"multiplier_0", "multiplier_1", "multiplier_2"}}, {"LensFlares", {"multiplier"}}}
    Private Shared ReadOnly DepthBlurParams As New Dictionary(Of String, String())(StringComparer.Ordinal) From {{"Dof", {"blurAmount"}}}
    Private Shared ReadOnly BinaryMagic As Byte() = {&H1A, &H22, &H52, &H72}
    Private Shared ReadOnly Value As New Regex("\A(\s*)(-?\d+(?:\.\d+)?)(\s*)\z", RegexOptions.CultureInvariant)
    Private Shared ReadOnly TextGroup As New Regex("(<ParameterGroup name=""BloomFromDsX4"">)(.*?)(</ParameterGroup>)", RegexOptions.CultureInvariant Or RegexOptions.Singleline)
    Private Shared ReadOnly TextMix As New Regex("(<Param name=""(?:mixMedium|mixLarge|mixHuge|mixStreak)"">\s*)(-?\d+(?:\.\d+)?)(\s*</Param>)", RegexOptions.CultureInvariant)

    Private Sub New()
    End Sub

    Public Shared Function TrackPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, "tracks", "generic", "effects.xml")
    End Function

    Public Shared Function NightPath(context As InstallContext) As String
        Return Path.Combine(context.GameRoot, "tracks", "generic", "night_effects.xml")
    End Function

    Public Shared Function IsBloomTarget(target As String) As Boolean
        Return target = TrackTarget OrElse target = NightTarget
    End Function

    ' Every file a post-processing edit can change: the effects file, bloom's track files and the colour palettes.
    Public Shared Function IsEffectTarget(target As String) As Boolean
        Return target = MotionBlur.Target OrElse IsBloomTarget(target) OrElse target = PaletteTarget
    End Function

    ' Whether a list of edits is a valid, non-empty selection in the session's order (the tint only with the palettes,
    ' the saturation only with both).
    Public Shared Function ValidEdits(edits As IReadOnlyList(Of String)) As Boolean
        If edits Is Nothing OrElse edits.Count = 0 Then Return False
        Dim positions = edits.Select(Function(edit) Array.IndexOf(Order, edit)).ToArray()
        Return positions.All(Function(position) position >= 0) AndAlso positions.Zip(positions.Skip(1), Function(a, b) a < b).All(Function(ordered) ordered) AndAlso
               (Not edits.Contains(TintEdit) OrElse edits.Contains(ColourEdit)) AndAlso (Not edits.Contains(SaturationEdit) OrElse edits.Contains(TintEdit))
    End Function

    ' The chosen edits of postprocess\effects.xml, applied in the session's order (the colour filter isn't in it).
    Public Shared Function Apply(data As Byte(), edits As IReadOnlyList(Of String)) As Byte()
        If Not ValidEdits(edits) Then Throw New IOException("Unknown post-processing edits.")
        Dim result = data
        For Each edit In edits
            Select Case edit
                Case MotionBlurEdit : result = MotionBlur.Disable(result)
                Case GlareEdit : result = DisableGlare(result)
                Case BloomEdit : result = DisableBloom(result)
                Case ColourEdit ' postprocess\palettes.pssg (ColourFilter)
                Case TintEdit : result = RemoveTint(result)
                Case SaturationEdit : result = NeutralSaturation(result)
                Case DepthBlurEdit : result = DisableDepthBlur(result)
            End Select
        Next
        Return result
    End Function

    ' Every Streaks and LensFlares multiplier set to zero (postprocess\effects.xml, binary).
    Public Shared Function DisableGlare(data As Byte()) As Byte()
        Return SetBinary(data, GlareParams, Zero, "No light streak or lens flare multipliers in this effects file.")
    End Function

    ' Every depth of field blurAmount set to zero (postprocess\effects.xml, binary).
    Public Shared Function DisableDepthBlur(data As Byte()) As Byte()
        Return SetBinary(data, DepthBlurParams, Zero, "No depth of field blur amounts in this effects file.")
    End Function

    ' Every BloomFromDsX4 mix set to zero, in a binary or a text effects file.
    Public Shared Function DisableBloom(data As Byte()) As Byte()
        If IsBinary(data) Then Return SetBinary(data, New Dictionary(Of String, String())(StringComparer.Ordinal) From {{BloomGroup, BloomMixes}}, Zero, "No bloom mixes in this effects file.")
        Return DisableTextBloom(data)
    End Function

    ' Every track effect's ColourBalance tintAmount set to zero (postprocess\effects.xml, binary). A file without track
    ' tints comes back unchanged: the palettes still do most of the colour filter's work.
    Public Shared Function RemoveTint(data As Byte()) As Byte()
        Return SetBinary(data, TintParams, Zero, "", tracksOnly:=True)
    End Function

    ' Every track effect's ColourBalance colour (saturation) set to 1.0, as RemoveTint. With the tint gone DiRT 3's
    ' own values (about 0.5 to 0.75) look flat (Rowan's Finland run); the remake mod raises them to about 1.0 too.
    Public Shared Function NeutralSaturation(data As Byte()) As Byte()
        Return SetBinary(data, SaturationParams, One, "", tracksOnly:=True)
    End Function

    ' The values the edits would set, in file order (for tests and diagnostics): group/parameter and value.
    Public Shared Function Read(data As Byte(), edit As String) As IReadOnlyList(Of (Name As String, Value As Double))
        If edit = TintEdit OrElse edit = SaturationEdit Then
            Dim effects = SeatCameras.Decode(data)
            Return ParamElements(effects, If(edit = TintEdit, TintParams, SaturationParams), "", tracksOnly:=True).Select(Function(item) (item.Name, Number(Value.Match(ElementText(effects, item.Element)).Groups(2).Value))).ToArray()
        End If
        Dim groups = If(edit = GlareEdit, GlareParams, If(edit = DepthBlurEdit, DepthBlurParams, New Dictionary(Of String, String())(StringComparer.Ordinal) From {{BloomGroup, BloomMixes}}))
        If Not IsBinary(data) Then
            If edit <> BloomEdit Then Throw New IOException("Not a binary effects file.")
            Dim found As New List(Of (Name As String, Value As Double))
            For Each group As Match In TextGroup.Matches(Strict.GetString(data))
                For Each mix As Match In Regex.Matches(group.Groups(2).Value, "<Param name=""(mix\w+)"">\s*(-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)
                    If BloomMixes.Contains(mix.Groups(1).Value) Then found.Add((BloomGroup & "/" & mix.Groups(1).Value, Number(mix.Groups(2).Value)))
                Next
            Next
            Return found
        End If
        Dim document = SeatCameras.Decode(data)
        Return ParamElements(document, groups, "").Select(Function(item) (item.Name, Number(Value.Match(ElementText(document, item.Element)).Groups(2).Value))).ToArray()
    End Function

    Private Shared ReadOnly Strict As New UTF8Encoding(False, True)

    Private Shared Function IsBinary(data As Byte()) As Boolean
        Return data IsNot Nothing AndAlso data.Length >= 4 AndAlso data.Take(4).SequenceEqual(BinaryMagic)
    End Function

    Private Shared Function Named(document As SeatCameras.BinXml, element As Integer, tag As String) As IEnumerable(Of (Element As Integer, Name As String))
        Return document.Children(element).Where(Function(child) document.Name(child) = tag).
            Select(Function(child)
                       Dim name As String = Nothing
                       Return (child, If(document.AttributesOf(child).TryGetValue("name", name), name, ""))
                   End Function).ToList()
    End Function

    ' Every Param named in groups (ParameterGroup name → Param names) under each Effect (each track effect with
    ' tracksOnly), in file order.
    Private Shared Function ParamElements(document As SeatCameras.BinXml, groups As IReadOnlyDictionary(Of String, String()), missing As String, Optional tracksOnly As Boolean = False) As List(Of (Element As Integer, Name As String))
        If document.Elements.Count = 0 OrElse document.Name(0) <> "PostProcessEffects" Then Throw New IOException("Not a post-processing effects file.")
        Dim found As New List(Of (Element As Integer, Name As String))
        For Each effect In Named(document, 0, "Effect")
            If tracksOnly AndAlso Not effect.Name.StartsWith(TrackEffect, StringComparison.Ordinal) Then Continue For
            For Each group In Named(document, effect.Element, "ParameterGroup")
                Dim names As String() = Nothing
                If Not groups.TryGetValue(group.Name, names) Then Continue For
                found.AddRange(Named(document, group.Element, "Param").Where(Function(item) names.Contains(item.Name)).Select(Function(item) (item.Element, group.Name & "/" & item.Name)))
            Next
        Next
        If found.Count = 0 AndAlso missing <> "" Then Throw New IOException(missing)
        Return found
    End Function

    Private Shared Function ElementText(document As SeatCameras.BinXml, element As Integer) As String
        Dim index = document.Elements(element)(1)
        Return If(index > 0, document.Text(index), "")
    End Function

    Private Shared Function Number(text As String) As Double
        Dim result As Double
        If Not Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, result) OrElse Not Double.IsFinite(result) Then Throw New IOException("Unexpected post-processing value.")
        Return result
    End Function

    ' Every Param named in groups set to wanted (Zero or One), keeping its whitespace; ones already there keep their bytes.
    Private Shared Function SetBinary(data As Byte(), groups As IReadOnlyDictionary(Of String, String()), wanted As String, missing As String, Optional tracksOnly As Boolean = False) As Byte()
        Dim document = SeatCameras.Decode(data)
        Dim target = Number(wanted)
        For Each item In ParamElements(document, groups, missing, tracksOnly)
            Dim match = Value.Match(ElementText(document, item.Element))
            If Not match.Success Then Throw New IOException("Unexpected post-processing value.")
            If Number(match.Groups(2).Value) = target Then Continue For
            Dim replacement = match.Groups(1).Value & wanted & match.Groups(3).Value
            ' Strings are shared between elements, so a new value never edits an existing string.
            Dim index = document.Strings.IndexOf(replacement)
            If index < 0 Then
                index = document.Strings.Count
                document.Strings.Add(replacement)
            End If
            document.Elements(item.Element)(1) = index
        Next
        Return SeatCameras.Encode(document)
    End Function

    ' tracks\generic\effects.xml: only the numbers inside the BloomFromDsX4 mix Params change.
    Private Shared Function DisableTextBloom(data As Byte()) As Byte()
        Dim content As String
        Try
            content = Strict.GetString(data)
        Catch ex As DecoderFallbackException
            Throw New IOException("Not a post-processing effects file.")
        End Try
        If Not content.Contains("<PostProcessEffects>", StringComparison.Ordinal) Then Throw New IOException("Not a post-processing effects file.")
        Dim seen = 0
        Dim result = TextGroup.Replace(content, Function(group)
                                                    Dim inner = TextMix.Replace(group.Groups(2).Value, Function(mix)
                                                                                                            seen += 1
                                                                                                            Return If(Number(mix.Groups(2).Value) = 0, mix.Value, mix.Groups(1).Value & Zero & mix.Groups(3).Value)
                                                                                                        End Function)
                                                    Return group.Groups(1).Value & inner & group.Groups(3).Value
                                                End Function)
        If seen = 0 Then Throw New IOException("No bloom mixes in this effects file.")
        Return Strict.GetBytes(result)
    End Function

    ' A session's post-processing changes: postprocess\effects.xml with the chosen edits (MotionBlur.Target), with
    ' bloom off the generic track files, and with the colour filter off its palettes and the track tints and saturation
    ' in the effects file. A file that is missing, unusable or already as wanted is left out; note says what stays on.
    ' Edits: the chosen edits (the journal records them).
    Public Shared Function SessionChanges(context As InstallContext, motionBlurOff As Boolean, glareOff As Boolean, bloomOff As Boolean, ByRef note As String, Optional colourOff As Boolean = False, Optional depthBlurOff As Boolean = False) As (Files As Dictionary(Of String, Byte()), Edits As String())
        note = ""
        Dim files As New Dictionary(Of String, Byte())
        Dim chosen = Order.Where(Function(edit) (edit = MotionBlurEdit AndAlso motionBlurOff) OrElse (edit = GlareEdit AndAlso glareOff) OrElse (edit = BloomEdit AndAlso bloomOff) OrElse ((edit = ColourEdit OrElse edit = TintEdit OrElse edit = SaturationEdit) AndAlso colourOff) OrElse (edit = DepthBlurEdit AndAlso depthBlurOff)).ToArray()
        If chosen.Length = 0 Then Return (files, chosen)
        Dim kept As New List(Of String)
        Dim effectEdits = chosen.Where(Function(edit) edit <> ColourEdit).ToArray()
        If effectEdits.Length > 0 Then
            Dim effects = Changed(MotionBlur.EffectsPath(context), Function(original) Apply(original, chosen))
            If effects.Usable Then
                If effects.Data IsNot Nothing Then files.Add(MotionBlur.Target, effects.Data)
            Else
                kept.Add(String.Join(", ", effectEdits.Where(Function(edit) edit <> SaturationEdit).Select(AddressOf Describe)) & " (postprocess\effects.xml)")
            End If
        End If
        If bloomOff Then
            For Each item In {(TrackTarget, TrackPath(context), "tracks\generic\effects.xml"), (NightTarget, NightPath(context), "tracks\generic\night_effects.xml")}
                Dim change = Changed(item.Item2, AddressOf DisableBloom)
                If Not change.Usable Then
                    kept.Add("bloom on some tracks (" & item.Item3 & ")")
                ElseIf change.Data IsNot Nothing Then
                    files.Add(item.Item1, change.Data)
                End If
            Next
        End If
        If colourOff Then
            Dim palettes = Changed(ColourFilter.PalettePath(context), AddressOf ColourFilter.Neutral, ColourFilter.MaximumSize)
            If Not palettes.Usable Then
                kept.Add("the colour filter (postprocess\palettes.pssg)")
            ElseIf palettes.Data IsNot Nothing Then
                files.Add(PaletteTarget, palettes.Data)
            End If
        End If
        If kept.Count > 0 Then note = "DiRT 3's effect files are missing or not in the expected format, so these stay on: " & String.Join("; ", kept) & "."
        Return (files, chosen)
    End Function

    Private Shared Function Describe(edit As String) As String
        Return If(edit = MotionBlurEdit, "motion blur", If(edit = GlareEdit, "light streaks and lens flares", If(edit = ColourEdit, "the colour filter", If(edit = TintEdit, "the colour filter's tint and saturation", If(edit = DepthBlurEdit, "depth blur", "bloom")))))
    End Function

    ' The changed bytes of one file (Nothing when nothing changes), or not usable when it is missing or unexpected.
    Private Shared Function Changed(target As String, transform As Func(Of Byte(), Byte()), Optional maximumSize As Long = 4 * 1024 * 1024) As (Usable As Boolean, Data As Byte())
        Try
            SafeFiles.NoLinks(target)
            If Not File.Exists(target) OrElse New FileInfo(target).Length > maximumSize Then Return (False, Nothing)
            Dim original = File.ReadAllBytes(target)
            Dim result = transform(original)
            Return (True, If(result.SequenceEqual(original), Nothing, result))
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            Return (False, Nothing)
        End Try
    End Function
End Class
