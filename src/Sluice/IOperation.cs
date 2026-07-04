namespace Sluice;

public interface IOperation
{
    public string Name { get; }
    public int Version { get; }
    public Type KeyType { get; }
    public Type ValueType { get; }
}
