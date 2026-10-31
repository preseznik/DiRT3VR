Imports System.Buffers.Binary

Public Module StartupMovies
    Public Const OriginalHash As String = "353f2b8fd00a8b26d47c74c446f07f1ec1ae38cf497482b426c0547bf2159996"
    Public Const AppliedHash As String = "929d24ec607126b9c99d3eb5b61e723fba630b5addc8561abf3569d39c90fe7b"
    Public Const OriginalLength As Integer = 590972

    Public Function Patch(original As Byte()) As Byte()
        If original Is Nothing OrElse original.Length <> OriginalLength OrElse SafeFiles.HashBytes(original) <> OriginalHash Then
            Throw New IOException("Skipping startup logos requires the supported original system\states.bin. The existing file was preserved.")
        End If
        Dim patched = DirectCast(original.Clone(), Byte())
        ' Verified BinXml element/attribute indices, not executable offsets.
        ' Replace only sting_video and amd_sting_video with StateWayPoint(id, timeDelay=0.0).
        ' Keep both tables, all strings, and every unrelated node byte-identical.
        ' Reproduction and independent semantic comparison: inspect_startup_movies.py.
        Dim edits As Integer(,) = {{130396, 572, 2318}, {130404, 4, 2}, {433468, 574, 1291}, {433472, 575, 3740},
                                  {130420, 572, 2318}, {130428, 4, 2}, {433500, 574, 1291}, {433504, 579, 3740}}
        For index = 0 To edits.GetLength(0) - 1
            Dim offset = edits(index, 0)
            If BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(offset, 4)) <> edits(index, 1) Then Throw New IOException("Unexpected startup state structure.")
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(offset, 4), edits(index, 2))
        Next
        If SafeFiles.HashBytes(patched) <> AppliedHash Then Throw New IOException("Startup patch verification failed.")
        Return patched
    End Function

    Public Function ReadPatch(context As InstallContext) As Byte()
        Dim target = Path.Combine(context.GameRoot, "system", "states.bin")
        SafeFiles.NoLinks(target)
        Using stream As New FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)
            If stream.Length <> OriginalLength Then Throw New IOException("Unsupported startup definitions. The existing file was preserved.")
            Dim bytes(OriginalLength - 1) As Byte
            stream.ReadExactly(bytes)
            Return Patch(bytes)
        End Using
    End Function
End Module
