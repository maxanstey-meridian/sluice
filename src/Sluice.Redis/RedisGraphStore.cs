using System.Text;
using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisGraphStore(IConnectionMultiplexer redis, string keyPrefix = "sluice")
    : IGraphStore
{
    private readonly IDatabase _db = redis.GetDatabase();

    private const string FwdSegment = ":fwd:";
    private const string RevSegment = ":rev:";

    public async Task<IReadOnlyList<string>> FindAffectedEntries(
        IReadOnlyList<ResourceAddress> changedAddresses,
        CancellationToken ct
    )
    {
        var affected = new HashSet<string>();

        foreach (var address in changedAddresses)
        {
            if (address.Key == "*")
            {
                var pattern =
                    $"{keyPrefix}{RevSegment}{address.Kind.ToString().ToLower()}:{address.Name}:*";
                var matchedKeys = await redis.ScanKeysAsync(pattern);
                foreach (var matchedKey in matchedKeys)
                {
                    var members = await _db.SetMembersAsync(matchedKey);
                    foreach (var member in members)
                    {
                        var memberStr = (string?)member;
                        if (memberStr is not null)
                        {
                            affected.Add(memberStr);
                        }
                    }
                }
            }
            else
            {
                var members = await _db.SetMembersAsync($"{keyPrefix}{RevSegment}{address}");
                foreach (var member in members)
                {
                    var memberStr = (string?)member;
                    if (memberStr is not null)
                    {
                        affected.Add(memberStr);
                    }
                }
            }
        }

        return affected.ToArray();
    }

    public async Task RecordEntry(
        string entryKey,
        IReadOnlyList<ResourceAddress> addresses,
        DateTimeOffset cachedAt,
        CancellationToken ct
    )
    {
        var batch = _db.CreateBatch();
        var tasks = new List<Task>();

        foreach (var address in addresses)
        {
            tasks.Add(batch.SetAddAsync($"{keyPrefix}{RevSegment}{address}", entryKey));
        }

        var addressValues = addresses.Select(a => (RedisValue)a.ToString()).ToArray();
        tasks.Add(batch.SetAddAsync($"{keyPrefix}{FwdSegment}{entryKey}", addressValues));
        tasks.Add(batch.StringSetAsync($"{keyPrefix}:ts:{entryKey}", cachedAt.ToString("O")));

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    public async Task ClearEntryEdges(string entryKey, CancellationToken ct)
    {
        var fwdKey = $"{keyPrefix}{FwdSegment}{entryKey}";
        var addresses = await _db.SetMembersAsync(fwdKey);
        foreach (var address in addresses)
        {
            var revKey = $"{keyPrefix}{RevSegment}{(string?)address!}";
            await _db.SetRemoveAsync(revKey, entryKey);
            var count = await _db.SetLengthAsync(revKey);
            if (count == 0)
            {
                await _db.KeyDeleteAsync(revKey);
            }
        }
        await _db.KeyDeleteAsync([fwdKey, $"{keyPrefix}:ts:{entryKey}"]);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var keys = new List<RedisKey>();
        keys.AddRange(await redis.ScanKeysAsync($"{keyPrefix}{RevSegment}*"));
        keys.AddRange(await redis.ScanKeysAsync($"{keyPrefix}{FwdSegment}*"));
        keys.AddRange(await redis.ScanKeysAsync($"{keyPrefix}:ts:*"));

        if (keys.Count > 0)
        {
            await _db.KeyDeleteAsync([.. keys]);
        }
    }

    public async Task<string> DumpGraphAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OPERATIONS:");

        var fwdPrefix = $"{keyPrefix}{FwdSegment}";
        var fwdKeys = await redis.ScanKeysAsync($"{fwdPrefix}*");
        foreach (var fk in fwdKeys)
        {
            var entryKey = ((string?)fk)?[fwdPrefix.Length..];
            if (entryKey is null)
            {
                continue;
            }
            sb.AppendLine($"  {entryKey}");
            sb.AppendLine("    reads:");
            var addresses = await _db.SetMembersAsync(fk);
            foreach (var addr in addresses)
            {
                var addrStr = (string?)addr;
                if (addrStr is not null)
                {
                    sb.AppendLine($"      {addrStr}");
                }
            }
            var tsKey = $"{keyPrefix}:ts:{entryKey}";
            var ts = await _db.StringGetAsync(tsKey);
            if (ts.HasValue)
            {
                sb.AppendLine($"    cached: {(string?)ts}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("RESOURCE ADDRESSES:");

        var revPrefix = $"{keyPrefix}{RevSegment}";
        var revKeys = await redis.ScanKeysAsync($"{revPrefix}*");
        foreach (var rk in revKeys)
        {
            var stripped = ((string?)rk)?[revPrefix.Length..];
            if (stripped is null)
            {
                continue;
            }
            sb.AppendLine($"  {stripped}");
            sb.AppendLine("    invalidates:");
            var members = await _db.SetMembersAsync(rk);
            foreach (var m in members)
            {
                var mStr = (string?)m;
                if (mStr is not null)
                {
                    sb.AppendLine($"      {mStr}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
