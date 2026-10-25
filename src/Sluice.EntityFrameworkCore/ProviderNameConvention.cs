using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Sluice.EntityFrameworkCore;

/// <summary>
/// Records a <c>UseSluice</c> context's database provider on its model, by the name EF's own provider checks
/// (<c>IsSqlite()</c>, <c>IsNpgsql()</c>) compare, so the model check can tell whose column a mapping EF shares between
/// providers maps (SQLite's <see cref="TimeSpan"/> is EF's own <c>TimeSpanTypeMapping</c>).
/// </summary>
internal sealed class ProviderNameConvention(IDatabaseProvider provider) : IConventionSetPlugin
{
    internal const string Annotation = "Sluice:Provider";

    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelFinalizingConventions.Add(new Convention(provider.Name));
        return conventionSet;
    }

    private sealed class Convention(string name) : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder,
            IConventionContext<IConventionModelBuilder> context
        ) => modelBuilder.HasAnnotation(Annotation, name);
    }
}
