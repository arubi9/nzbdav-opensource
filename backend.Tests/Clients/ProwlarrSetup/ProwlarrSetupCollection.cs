namespace NzbWebDAV.Tests.Clients.ProwlarrSetup;

// ProwlarrSetupClient serializes all mutations through a process-global gate;
// these tests must not contend with unrelated tests of that same static gate.
[CollectionDefinition(nameof(ProwlarrSetupCollection), DisableParallelization = true)]
public sealed class ProwlarrSetupCollection;
