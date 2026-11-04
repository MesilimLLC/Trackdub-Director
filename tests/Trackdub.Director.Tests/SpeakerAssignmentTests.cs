using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class SpeakerAssignmentTests
{
    [Fact]
    public void Resolves_asr_label_to_user_selected_cast_voice_case_insensitively()
    {
        var mapping = new Dictionary<string, string> { ["A"] = "anna" };

        Assert.Equal("anna", SpeakerAssignment.Resolve("a", mapping));
    }

    [Fact]
    public void Preserves_unmapped_source_label_for_visible_unassigned_state()
    {
        var mapping = new Dictionary<string, string> { ["A"] = "" };

        Assert.Equal("B", SpeakerAssignment.Resolve("B", mapping));
        Assert.Equal("A", SpeakerAssignment.Resolve("A", mapping));
        Assert.Null(SpeakerAssignment.Resolve(null, mapping));
    }
}
