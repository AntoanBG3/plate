using FluentAssertions;
using Plate.Core.Text;

namespace Plate.Core.Tests;

/// <summary>Browser messages that aren't app errors stay out of the error log (SPEC 11, Phase 11).</summary>
public class ErrorNoiseTests
{
    [Theory]
    [InlineData("Transition was aborted because of invalid state. Document hidden")]
    [InlineData("Transition was aborted because of invalid state. Viewport size changed")]
    [InlineData("AbortError: Transition was skipped")]
    [InlineData("InvalidStateError: Transition was aborted because of invalid state")]
    [InlineData("Skipping view transition because document visibility state is hidden.")]
    [InlineData("Skipping view transition because skipTransition() was called.")]
    [InlineData("Old view transition aborted by new view transition.")]
    public void View_transition_aborts_are_noise(string message) => ErrorNoise.IsIgnored(message).Should().BeTrue();

    [Theory]
    [InlineData("TypeError: Cannot read properties of null (reading 'value')")]
    [InlineData("ReferenceError: gymToken is not defined")]
    public void Real_errors_are_kept(string message) => ErrorNoise.IsIgnored(message).Should().BeFalse();
}
