using System.Text;
using StackExchange.Redis;

namespace Sluice.Redis;

public sealed class RedisGraphStore(IConnectionMultiplexer redis, string keyPrefix = "sluice")
    : IGraphStore
{
    private readonly IDatabase _db = redis.GetDatabase();

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
                var cursor = 0L;
                do
                {
                    var result = await _db.ExecuteAsync(
                        "SCAN",
                        cursor.ToString(),
                        "MATCH",
                        $"{keyPrefix}:rev:{address.Kind.ToString().ToLower()}:{address.Name}:*",
                        "COUNT",
                        "500"
                    );
                    var inner = (RedisResult[])result!;
                    cursor = long.Parse((string?)inner[0] ?? "0");
                    var matchedKeys = (RedisResult[])inner[1]!;
                    foreach (var matchedKey in matchedKeys)
                    {
                        var keyStr = (string?)matchedKey;
                        if (keyStr is null)
                        {
                            continue;
                        }
                        var members = await _db.SetMembersAsync(keyStr);
                        foreach (var member in members)
                        {
                            var memberStr = (string?)member;
                            if (memberStr is not null)
                            {
                                affected.Add(memberStr);
                            }
                        }
                    }
                } while (cursor != 0);
            }
            else
            {
                var members = await _db.SetMembersAsync($"{keyPrefix}:rev:{address}");
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
        foreach (var address in addresses)
        {
            await _db.SetAddAsync($"{keyPrefix}:rev:{address}", entryKey);
        }
        var fwdKey = $"{keyPrefix}:fwd:{entryKey}";
        var addressStrings = addresses.Select(a => a.ToString()).ToArray();
        await _db.SetAddAsync(fwdKey, addressStrings.Select<string, RedisValue>(a => a).ToArray());
        await _db.StringSetAsync(
            $"{keyPrefix}:ts:{entryKey}",
            cachedAt.ToString("O"),
            null,
            false,
            When.Always,
            CommandFlags.None
        );
    }

    public async Task ClearEntryEdges(string entryKey, CancellationToken ct)
    {
        var fwdKey = $"{keyPrefix}:fwd:{entryKey}";
        var addresses = await _db.SetMembersAsync(fwdKey);
        foreach (var address in addresses)
        {
            var revKey = $"{keyPrefix}:rev:{(string?)address!}";
            await _db.SetRemoveAsync(revKey, entryKey);
            var count = await _db.SetLengthAsync(revKey);
            if (count == 0)
            {
                await _db.KeyDeleteAsync(revKey);
            }
        }
        await _db.KeyDeleteAsync(fwdKey);
        await _db.KeyDeleteAsync($"{keyPrefix}:ts:{entryKey}");
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var keys = new List<RedisKey>();
        var cursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                $"{keyPrefix}:rev:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            var items = (RedisResult[])inner[1]!;
            foreach (var item in items)
            {
                var bytes = (byte[]?)(RedisResult?)item;
                keys.Add(bytes is not null ? (RedisKey)bytes : new RedisKey((string?)item!));
            }
        } while (cursor != 0);

        cursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                $"{keyPrefix}:fwd:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            var items = (RedisResult[])inner[1]!;
            foreach (var item in items)
            {
                var bytes = (byte[]?)(RedisResult?)item;
                keys.Add(bytes is not null ? (RedisKey)bytes : new RedisKey((string?)item!));
            }
        } while (cursor != 0);

        cursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                cursor.ToString(),
                "MATCH",
                $"{keyPrefix}:ts:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            cursor = long.Parse((string?)inner[0] ?? "0");
            var items = (RedisResult[])inner[1]!;
            foreach (var item in items)
            {
                var bytes = (byte[]?)(RedisResult?)item;
                keys.Add(bytes is not null ? (RedisKey)bytes : new RedisKey((string?)item!));
            }
        } while (cursor != 0);

        if (keys.Count > 0)
        {
            await _db.KeyDeleteAsync([.. keys]);
        }
    }

    public async Task<string> DumpGraphAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OPERATIONS:");

        var fwdCursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                fwdCursor.ToString(),
                "MATCH",
                $"{keyPrefix}:fwd:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            fwdCursor = long.Parse((string?)inner[0] ?? "0");
            var fwdKeys = (RedisResult[])inner[1]!;
            foreach (var fk in fwdKeys)
            {
                var entryKey = (string?)fk;
                if (entryKey is null)
                {
                    continue;
                }
                var stripped = entryKey.Substring(keyPrefix.Length + 5);
                sb.AppendLine($"  {stripped}");
                sb.AppendLine("    reads:");
                var addresses = await _db.SetMembersAsync(entryKey);
                foreach (var addr in addresses)
                {
                    var addrStr = (string?)addr;
                    if (addrStr is not null)
                    {
                        sb.AppendLine($"      {addrStr}");
                    }
                }
                var tsKey = $"{keyPrefix}:ts:{stripped}";
                var ts = await _db.StringGetAsync(tsKey);
                if (ts.HasValue)
                {
                    sb.AppendLine($"    cached: {(string?)ts}");
                }
                sb.AppendLine();
            }
        } while (fwdCursor != 0);

        sb.AppendLine("RESOURCE ADDRESSES:");
        var revCursor = 0L;
        do
        {
            var result = await _db.ExecuteAsync(
                "SCAN",
                revCursor.ToString(),
                "MATCH",
                $"{keyPrefix}:rev:*",
                "COUNT",
                "500"
            );
            var inner = (RedisResult[])result!;
            revCursor = long.Parse((string?)inner[0] ?? "0");
            var revKeys = (RedisResult[])inner[1]!;
            foreach (var rk in revKeys)
            {
                var addressStr = (string?)rk;
                if (addressStr is null)
                {
                    continue;
                }
                var stripped = addressStr.Substring(keyPrefix.Length + 5);
                sb.AppendLine($"  {stripped}");
                sb.AppendLine("    invalidates:");
                var members = await _db.SetMembersAsync(addressStr);
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
        } while (revCursor != 0);

        return sb.ToString();
    }
}
