namespace Sluice;

public sealed record OperationInfo(
    string Name,
    string InputType,
    string OutputType,
    string DefinedBy
);
