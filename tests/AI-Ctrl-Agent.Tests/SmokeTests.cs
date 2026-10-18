using Shouldly;

namespace AiCtrl.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void TestInfrastructure_IsOperational()
    {
        true.ShouldBeTrue();
    }
}
