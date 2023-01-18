using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Devlooped.SponsorLink;

public class Functions
{
    readonly SponsorsManager manager;
    readonly IConfiguration configuration;
    readonly IHttpClientFactory clientFactory;

    readonly EventStream events;

    public Functions(SponsorsManager manager, IConfiguration configuration, IHttpClientFactory clientFactory, EventStream events)
        => (this.manager, this.configuration, this.clientFactory, this.events)
        = (manager, configuration, clientFactory, events);

    record EmailInfo(string email, bool verified);

    public record PingCompleted(DateTimeOffset When);

    [FunctionName("ping")]
    public async Task<IActionResult> Ping([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequest req)
    {
        await events.PushAsync(new PingCompleted(DateTimeOffset.Now));
        return new OkObjectResult("pong");
    }

    [FunctionName("expirations")]
    public async Task CheckExpirationsAsync([TimerTrigger("0 0 0 * * *")] TimerInfo timer)
        => await manager.CheckExpirationsAsync();

    [FunctionName("authorize")]
    public async Task<IActionResult> AuthorizeAppAsync([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "authorize/{kind}")] HttpRequest req, string kind)
    {
        var appKind = Enum.Parse<AppKind>(kind, true);
        var code = req.Query["code"].ToString();
        
        // TODO: the installation id can be used to request an installation token to perform actions 
        // authorized to the app on behalf of the authorizing user.
        // This will be necessary for the sponsorable account if we ever figure out how to run GraphQL 
        // queries, but for now, it's impossible.
        //var installation = req.Query["installation_id"].ToString();
        
        await manager.AuthorizeAsync(appKind, code);

        return new RedirectResult("https://devlooped.com");
    }

    [FunctionName("app")]
    public async Task<IActionResult> AppHookAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "app/{kind}")] HttpRequest req, string kind)
    {
        using var reader = new StreamReader(req.Body);
        dynamic? payload = new JsonSerializer().Deserialize(new JsonTextReader(reader));

        if (payload == null)
            return new BadRequestObjectResult("Could not deserialize payload as JSON");

        string action = payload.action;
        var appKind = Enum.Parse<AppKind>(kind, true);
        var id = new AccountId((string)payload.installation.account.node_id, (string)payload.installation.account.login);
        var note = $"App {appKind} {action} on {payload.installation.account.login} by {payload.sender.login}";

        await (action switch
        {
            "created" => manager.AppInstallAsync(appKind, id, note),
            "deleted" => manager.AppUninstallAsync(appKind, id, note),
            "suspend" => manager.AppSuspendAsync(appKind, id, note),
            "unsuspend" => manager.AppUnsuspendAsync(appKind, id, note),
            _ => Task.CompletedTask,
        });

        return new OkObjectResult(note);
    }

    [FunctionName("sponsorable")]
    public async Task<IActionResult> SponsorHookAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sponsors/{account}")] HttpRequest req, string account)
    {
        using var reader = new StreamReader(req.Body);
        dynamic? payload = new Newtonsoft.Json.JsonSerializer().Deserialize(new JsonTextReader(reader));

        if (payload == null)
            return new BadRequestObjectResult("Could not deserialize payload as JSON");

        string action = payload.action;
        var sponsorable = new AccountId((string)payload.sponsorable.node_id, (string)payload.sponsorable.login);
        var sponsor = new AccountId((string)payload.sponsor.node_id, (string)payload.sponsor.login);
        int amount = payload.sponsorship.tier.monthly_price_in_dollars;
        bool oneTime = payload.sponsorship.tier.is_one_time;
        DateTime date = payload.sponsorship.created_at;

        if (action == "created")
        {
            var note = $"{sponsor.Login} started sponsoring {sponsorable.Login} with ${amount}";
            if (oneTime)
                note += " (one-time)";

            await manager.SponsorAsync(sponsorable, sponsor, amount, oneTime ? DateOnly.FromDateTime(date).AddDays(30) : null, note);
        }
        else if (action == "pending_cancellation")
        {
            DateTime cancelAt = payload.effective_date;
            await manager.UnsponsorAsync(sponsorable, sponsor, DateOnly.FromDateTime(cancelAt),
                $"{sponsor.Login} is cancelling sponsorship of {sponsorable.Login} by ${DateOnly.FromDateTime(cancelAt)}");
        }
        else if (action == "tier_changed")
        {
            int from = payload.changes.tier.from.monthly_price_in_dollars;
            var note = $"{sponsor.Login} updated sponsorship of {sponsorable.Login} from ${from} to ${amount}";

            await manager.SponsorUpdateAsync(sponsorable, sponsor, amount, note);
        }

        // TODO: if sponsorable has not installed the admin app or is 
        // not sponsoring devlooped with at least $x, return OK with content explaining 
        // this and that checks won't work until they do.


        //var body = await new StreamReader(req.Body).ReadToEndAsync();
        //var json = JToken.Parse(body).ToString(Formatting.Indented);

        //var id = req.Headers["X-GitHub-Delivery"].ToString();
        //// The delivery identifier is the most precise. But if we happen to not get the 
        //// webhook from GH, we just generate a unique identifier based on the payload itself
        //// This avoids duplicating entries even from retries.
        //if (string.IsNullOrEmpty(id))
        //    id = Base62.Encode(BigInteger.Abs(new BigInteger(SHA1.HashData(Encoding.UTF8.GetBytes(body)))));

        //var type = req.Headers["X-GitHub-Event"].ToString();
        //if (string.IsNullOrEmpty(type))
        //    type = "general";

        //subject ??= "general";

        //await repository.PutAsync(new TableEntity($"{topic}.{subject}", $"{type}.{id}")
        //{
        //    ["body"] = json
        //});

        //var key = configuration["EventGridAccessKey"];
        //var domain = configuration["EventGridDomain"];
        //if (key != null && domain != null)
        //{
        //    using var client = new EventGridClient(new TopicCredentials(key));
        //    await client.PublishEventsAsync(domain, new List<EventGridEvent>
        //    {
        //        new EventGridEvent(
        //            id: id,
        //            subject: subject,
        //            topic: topic,
        //            data: json,
        //            eventType: type,
        //            eventTime: DateTime.UtcNow,
        //            dataVersion: "1.0")
        //    });
        //}

        return new OkResult();
    }

    //[FunctionName("sponsor")]
    //public async Task Sponsors([EventGridTrigger] EventGridEvent e)
    //{
    //    // Event path is: webhook/[topic:devlooped]/[subject:sponsors]/[event:sponsorship]
    //    // As configured on the dashboard at https://github.com/sponsors/devlooped/dashboard/webhooks/396006910/edit
    //    if (!e.Topic.EndsWith("/devlooped") ||
    //        e.EventType != "sponsorship" ||
    //        e.Subject != "sponsors")
    //        return;

    //    using var reader = new StreamReader(e.Data.ToStream());
    //    var payload = await reader.ReadToEndAsync();

    //    File.WriteAllText($"sponsor-{DateTimeOffset.Now.ToQuaranTimeSeconds()}.json", payload);
    //}

    //[FunctionName("sponsor")]
    //public static async Task<IActionResult> Sponsors(
    //    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req)
    //{
    //    using var reader = new StreamReader(req.Body);
    //    var payload = await reader.ReadToEndAsync();

    //    File.WriteAllText($"sponsor-{DateTimeOffset.Now.ToQuaranTimeSeconds()}.json", payload);

    //    return new OkResult();
    //}
}
