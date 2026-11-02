namespace EufyNet.Tests;

/// <summary>
/// Tests with a <see cref="FakeStation"/> run one after another: on loopback they share 127.0.0.1, CHECK_CAM sweeps
/// neighbouring ports and ephemeral ports are reused, so a parallel test's station can answer another test's session.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LoopbackUdp
{
    public const string Name = "Loopback UDP";
}
