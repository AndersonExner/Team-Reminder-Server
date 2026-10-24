using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace TeamReminder.Server;

public sealed record AlarmDefinition(string Title, DateTimeOffset ScheduledAtUtc, DateTimeOffset CreatedAtUtc,
    string TimeZoneId, int Recurrence, int Days, string? LinkUrl, string? LinkLabel, int SoundKind);
public sealed record AlarmWriteRequest(long ExpectedVersion, Guid MutationId, bool Deleted, AlarmDefinition? Alarm);
public sealed record PersonalAlarmChange(Guid Id, long Version, bool Deleted, Guid MutationId, AlarmDefinition? Alarm);
public sealed record AlarmChangeSet(long Cursor, IReadOnlyList<PersonalAlarmChange> Changes);

public sealed class PersonalAlarm
{
    public Guid AccountId { get; set; }
    public Guid Id { get; set; }
    public long Version { get; set; }
    public Guid MutationId { get; set; }
    public bool Deleted { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int Recurrence { get; set; }
    public int Days { get; set; }
    public string? LinkUrl { get; set; }
    public string? LinkLabel { get; set; }
    public int SoundKind { get; set; }

    public PersonalAlarmChange ToChange() => new(Id, Version, Deleted, MutationId, Deleted ? null :
        new(Title, ScheduledAtUtc, CreatedAtUtc, TimeZoneId, Recurrence, Days, LinkUrl, LinkLabel, SoundKind));
}

public static class PersonalAlarmEndpoints
{
    public static void MapPersonalAlarms(this WebApplication app)
    {
        var alarms = app.MapGroup("/api/alarms").RequireAuthorization();
        alarms.MapGet("/", async (ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
        {
            var owner = Owner(principal);
            var rows = await db.PersonalAlarms.AsNoTracking().Where(x => x.AccountId == owner && !x.Deleted)
                .OrderBy(x => x.ScheduledAtUtc).ToListAsync(ct);
            return Results.Ok(rows.Select(x => x.ToChange()));
        });
        alarms.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
        {
            var owner = Owner(principal);
            var row = await db.PersonalAlarms.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == owner && x.Id == id && !x.Deleted, ct);
            return row is null ? Results.NotFound() : Results.Ok(row.ToChange());
        });
        alarms.MapPut("/{id:guid}", WriteAsync);
        alarms.MapDelete("/{id:guid}", (Guid id, long version, Guid mutationId, ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
            WriteAsync(id, new AlarmWriteRequest(version, mutationId, true, null), principal, db, ct));
        app.MapGet("/api/sync", async (long? since, ClaimsPrincipal principal, AuthDb db, CancellationToken ct) =>
        {
            if (since is < 0) return Results.BadRequest(new { message = "Cursor inválido." });
            var owner = Owner(principal);
            // A consistent snapshot prevents advancing past a concurrently committed mutation.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var cursor = await db.Accounts.Where(x => x.Id == owner).Select(x => x.AlarmRevision).SingleAsync(ct);
            if (since > cursor) return Results.BadRequest(new { message = "Reinicie a sincronização com cursor zero." });
            var rows = await db.PersonalAlarms.AsNoTracking().Where(x => x.AccountId == owner && x.Version > (since ?? 0) && x.Version <= cursor)
                .OrderBy(x => x.Version).ToListAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.Ok(new AlarmChangeSet(cursor, rows.Select(x => x.ToChange()).ToArray()));
        }).RequireAuthorization();
    }

    private static Guid Owner(ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<IResult> WriteAsync(Guid id, AlarmWriteRequest request, ClaimsPrincipal principal, AuthDb db, CancellationToken ct)
    {
        var errors = Validate(id, request);
        if (errors.Count != 0) return Results.ValidationProblem(errors);
        var owner = Owner(principal);
        if(await db.GroupAlarms.AnyAsync(x=>x.Id==id,ct))return Results.Conflict(new{message="Este identificador pertence a um alarme de grupo. Use a rota do grupo."});
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Each account has its own monotonic revision and serializes its writes.
        var accounts = await db.Accounts.FromSqlInterpolated($"SELECT * FROM \"Accounts\" WHERE \"Id\" = {owner} FOR UPDATE").ToListAsync(ct);
        var account = accounts.Single();
        if(!account.IsActive||account.DeletedAt!=null)return Results.Unauthorized();
        var row = await db.PersonalAlarms.SingleOrDefaultAsync(x => x.AccountId == owner && x.Id == id, ct);
        if (row is not null && row.MutationId == request.MutationId)
            return Results.Ok(row.ToChange());
        if ((row?.Version ?? 0) != request.ExpectedVersion)
            return Results.Conflict(new { message = "O alarme foi alterado em outro dispositivo.", current = row?.ToChange() });
        // Tombstones stay for incremental sync but do not count toward the limit; restoring one does.
        if (!request.Deleted && row is not { Deleted: false } &&
            await db.PersonalAlarms.CountAsync(x => x.AccountId == owner && !x.Deleted, ct) >= 1000)
            return Results.Json(new { message = "Limite de 1000 alarmes ativos por conta atingido." }, statusCode: 422);
        if (row is null)
        {
            if (await db.PersonalAlarms.CountAsync(x => x.AccountId == owner, ct) >= 10_000)
                return Results.Json(new { message = "Limite de registros da conta atingido, incluindo exclusões." }, statusCode: 422);
            row = new PersonalAlarm { Id = id, AccountId = owner };
            db.PersonalAlarms.Add(row);
        }
        if (request.Alarm is { } alarm && !request.Deleted)
        {
            row.Title = alarm.Title.Trim();
            row.ScheduledAtUtc = Milliseconds(alarm.ScheduledAtUtc);
            row.CreatedAtUtc = Milliseconds(alarm.CreatedAtUtc);
            row.TimeZoneId = alarm.TimeZoneId;
            row.Recurrence = alarm.Recurrence; row.Days = alarm.Days;
            row.LinkUrl = string.IsNullOrWhiteSpace(alarm.LinkUrl) ? null : new Uri(alarm.LinkUrl.Trim()).AbsoluteUri;
            row.LinkLabel = row.LinkUrl is null ? null : string.IsNullOrWhiteSpace(alarm.LinkLabel) ? "Abrir link" : alarm.LinkLabel.Trim();
            row.SoundKind = alarm.SoundKind;
        }
        row.Deleted = request.Deleted;
        row.MutationId = request.MutationId;
        row.Version = ++account.AlarmRevision;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(row.ToChange());
    }

    private static DateTimeOffset Milliseconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    public static Dictionary<string, string[]> Validate(Guid id, AlarmWriteRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (id == Guid.Empty || request.MutationId == Guid.Empty || request.ExpectedVersion < 0)
            errors["version"] = ["Informe identificadores e versão válidos."];
        if (request.Deleted) return errors;
        if (request.Alarm is not { } alarm) { errors["alarm"] = ["Informe o alarme."]; return errors; }
        if (string.IsNullOrWhiteSpace(alarm.Title) || alarm.Title.Trim().Length > 120)
            errors["title"] = ["Informe um título de 1 a 120 caracteres."];
        if (alarm.ScheduledAtUtc.Offset != TimeSpan.Zero || alarm.CreatedAtUtc.Offset != TimeSpan.Zero ||
            alarm.ScheduledAtUtc.Year is < 1900 or > 9990 || alarm.CreatedAtUtc.Year is < 1900 or > 9990)
            errors["schedule"] = ["Informe datas UTC válidas."];
        try
        {
            if (string.IsNullOrWhiteSpace(alarm.TimeZoneId) || alarm.TimeZoneId.Length > 128) throw new TimeZoneNotFoundException();
            TimeZoneInfo.FindSystemTimeZoneById(alarm.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { errors["timeZoneId"] = ["Informe um fuso horário válido."]; }
        if (alarm.Recurrence is < 0 or > 2 || alarm.Days is < 0 or > 127 ||
            (alarm.Recurrence == 2 ? alarm.Days == 0 : alarm.Days != 0))
            errors["recurrence"] = ["Informe uma recorrência válida."];
        if (alarm.SoundKind is < 0 or > 4) errors["sound"] = ["Arquivos de som personalizados permanecem locais."];
        if (!string.IsNullOrWhiteSpace(alarm.LinkUrl))
        {
            var url = alarm.LinkUrl.Trim();
            if (url.Length > 2048 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.AbsoluteUri.Length > 2048)
                errors["link"] = ["Informe um link HTTP/HTTPS válido sem credenciais."];
        }
        else if (!string.IsNullOrWhiteSpace(alarm.LinkLabel)) errors["link"] = ["Informe o link ou remova o texto do botão."];
        if (alarm.LinkLabel is { } label && (label.Trim().Length > 40 || label.Any(char.IsControl)))
            errors["linkLabel"] = ["O texto do botão deve ter até 40 caracteres, em uma linha."];
        return errors;
    }
}
