﻿using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Octokit;

namespace Devlooped.SponsorLink;

#pragma warning disable CS1998

[Service]
public class SponsorsManager
{
    static readonly ProductInfoHeaderValue httpProduct = new("SponsorLink", new Version(ThisAssembly.Info.Version).ToString(2));
    static readonly Octokit.ProductHeaderValue octoProduct = new("SponsorLink", new Version(ThisAssembly.Info.Version).ToString(2));

    readonly IHttpClientFactory httpFactory;
    readonly SecurityManager security;
    readonly TableConnection tableConnection;
    readonly IEventStream events;
    readonly SponsorsRegistry registry;
    readonly TableConnection sponsorshipsConnection;
    readonly ITableRepository<Account> accounts;


    public SponsorsManager(
        IHttpClientFactory httpFactory, SecurityManager security, 
        TableConnection tableConnection, IEventStream events, SponsorsRegistry registry)
    {
        (this.httpFactory, this.security, this.tableConnection, this.events, this.registry, sponsorshipsConnection) =
            (httpFactory, security, tableConnection, events, registry, new TableConnection(tableConnection.StorageAccount, nameof(Sponsorship)));

        accounts = TableRepository.Create<Account>(tableConnection);
    }

    public async Task AuthorizeAsync(AppKind kind, string code)
    {
        var auth = security.CreateAuthorization(kind, code);
        var jwt = security.IssueToken(kind);

        using var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(httpProduct);

        var resp = await http.PostAsync("https://github.com/login/oauth/access_token",
            new StringContent(auth, Encoding.UTF8, "application/json"), jwt);

        var payload = await resp.Content.ReadAsStringAsync();
        dynamic data = JsonConvert.DeserializeObject(payload) ??
            throw new InvalidOperationException("Failed to deserialize OAuth response as JSON:\r\n" + payload);

        try
        {
            string accessToken = data.access_token;
            if (string.IsNullOrEmpty(accessToken))
                throw new InvalidOperationException("OAuth response did not contain an access token:\r\n" + payload);

            var octo = new GitHubClient(octoProduct)
            {
                Credentials = new Credentials(accessToken)
            };

            var user = await octo.User.Current();
            var partition = TablePartition.Create<Authorization>(tableConnection);

            await partition.PutAsync(new Authorization(user.NodeId, accessToken, user.Login));
            await events.PushAsync(new UserAuthorized(user.NodeId, user.Login, kind));
            // Due to timing between AppInstall and Authorize, we need to do this refresh async
            await events.PushAsync(new UserRefreshPending(user.NodeId, user.Login, 0, "User installed SponsorLink"));
        }
        catch (RuntimeBinderException)
        {
            throw new InvalidOperationException("OAuth response did not contain an access token:\r\n" + payload);
        }
    }

    public Task<Installation?> FindAppAsync(AppKind kind, AccountId account)
    {
        var partition = TablePartition.Create<Installation>(tableConnection,
            kind == AppKind.Sponsorable ? "Sponsorable" : "Sponsor");

        return partition.GetAsync(account.Id);
    }

    public async IAsyncEnumerable<Installation> EnumerateInstallationsAsync(AppKind kind, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        var partition = TablePartition.Create<Installation>(tableConnection,
            kind == AppKind.Sponsorable ? "Sponsorable" : "Sponsor");

        await foreach (var installation in partition.EnumerateAsync(cancellation))
        {
            yield return installation;
        }
    }

    public async Task AppInstallAsync(AppKind kind, AccountId account, string? note = default)
    {
        var partition = TablePartition.Create<Installation>(tableConnection,
            kind == AppKind.Sponsorable ? "Sponsorable" : "Sponsor");

        var installation = new Installation(account.Id, account.Login, AppState.Installed, Guid.NewGuid().ToString());

        await partition.PutAsync(installation);
        await events.PushAsync(new AppInstalled(account.Id, account.Login, kind, note));
    }

    public async Task AppSuspendAsync(AppKind kind, AccountId account, string? note = default)
    {
        await ChangeState(kind, account, AppState.Suspended);
        await events.PushAsync(new AppSuspended(account.Id, account.Login, kind, note));
        if (kind == AppKind.Sponsor)
        {
            await events.PushAsync(new UserRefreshPending(account.Id, account.Login, 0, note)
            {
                Unregister = true
            });
        }
        else if (kind == AppKind.Sponsorable)
        {
            await SyncSponsorableAsync(account, true);
        }
    }

    public async Task AppUnsuspendAsync(AppKind kind, AccountId account, string? note = default)
    {
        await ChangeState(kind, account, AppState.Installed);
        await events.PushAsync(new AppUnsuspended(account.Id, account.Login, kind, note));
        if (kind == AppKind.Sponsor)
        {
            await events.PushAsync(new UserRefreshPending(account.Id, account.Login, 0, note));
        }
        else if (kind == AppKind.Sponsorable)
        {
            await SyncSponsorableAsync(account, false);
        }
    }

    public async Task AppUninstallAsync(AppKind kind, AccountId account, string? note = default)
    {
        await ChangeState(kind, account, AppState.Deleted);
        await events.PushAsync(new AppUninstalled(account.Id, account.Login, kind, note));
        if (kind == AppKind.Sponsor)
        {
            await events.PushAsync(new UserRefreshPending(account.Id, account.Login, 0, note)
            {
                Unregister = true
            });
        }
        else if (kind == AppKind.Sponsorable)
        {
            await SyncSponsorableAsync(account, true);
        }
    }

    public async Task<bool> SyncUserAsync(AccountId account, string? sponsorableId, bool unregister)
    {
        var bySponsor = TablePartition.Create<Sponsorship>(sponsorshipsConnection,
            $"Sponsor-{account.Id}", x => x.SponsorableId);

        var done = true;

        if (sponsorableId == null)
        {
            // If we're not doing sponsorable-based refresh, we need to sync the app 
            // registration for the user account too.
            done &= await UpdateAppRegistryAsync(account);
        }

        await foreach (var sponsorship in bySponsor.EnumerateAsync())
        {
            // If we're filtering by sponsorable, skip non-matches.
            if (sponsorableId != null && sponsorship.SponsorableId != sponsorableId)
                continue;

            var sponsorable = new AccountId(sponsorship.SponsorableId, sponsorship.SponsorableLogin);
            if (!unregister)
                // When unregistering, we always clear stuff, regardless of sponsorable verification.
                await VerifySponsorableAsync(sponsorable);

            if (unregister)
                await registry.UnregisterSponsorAsync(sponsorable, account);
            else
                done &= await UpdateSponsorRegistryAsync(sponsorable, account);
        }

        // Mark the user as "sponsor" (technically, supporter?) of their orgs.
        // We can only perform this query *after* authorization, since we'll need the token
        if (await TablePartition.Create<Authorization>(tableConnection).GetAsync(account.Id) is Authorization auth)
        {
            var query =
                """
                query {
                  viewer{
                    organizations(first: 100) {
                      nodes {
                        id
                        login
                      }
                    }
                  }
                }
                """;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SponsorLink", new Version(ThisAssembly.Info.Version).ToString(2)));
            var response = await http.PostAsJsonAsync("https://api.github.com/graphql", new { query });
            // Make sure we don't fail.
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                foreach (var org in JObject.Parse(body)
                    .SelectTokens("$.data.viewer.organizations.nodes[*]")
                    .Select(j => j.ToString())
                    .Select(JsonConvert.DeserializeObject<AccountId>)
                    .Where(x => x != null))
                {
                    if (org is null)
                        continue;

                    if (unregister)
                        await registry.UnregisterSponsorAsync(org, account);
                    else
                        // NOTE: we don't update the done flag, since the given org might not be sponsorable
                        // at all, may not have the SL Admin app installed, etc. These are all expected situations.
                        await UpdateSponsorRegistryAsync(org, account, member: true);

                    // Special case for Moq > Devlooped sponsorship. It's the only org whose repos we haven't 
                    // moved over to develooped, so we need to sync it too, for now.
                    if (org.Id == Constants.MoqAccount.Id)
                    {
                        // Fake devlooped organization belonging for the user.
                        if (unregister)
                            await registry.UnregisterSponsorAsync(Constants.DevloopedAccount, account);
                        else
                            await UpdateSponsorRegistryAsync(Constants.DevloopedAccount, account, member: true);
                    }
                }
            }
        }

        return done;
    }

    public async Task SyncSponsorableAsync(AccountId sponsorable, bool unregister)
    {
        // When unregistering, we cannot verify sponsorable.
        if (!unregister)
            await VerifySponsorableAsync(sponsorable);

        var bySponsorable = TablePartition.Create<Sponsorship>(sponsorshipsConnection,
            $"Sponsorable-{sponsorable.Id}", x => x.SponsorId);

        await foreach (var sponsorship in bySponsorable.EnumerateAsync())
        {
            // Schedules refresh for the given sponsor, but only for our sponsorable
            await events.PushAsync(new UserRefreshPending(sponsorship.SponsorId, sponsorship.SponsorLogin, 0)
            {
                Sponsorable = sponsorable.Id,
                Unregister = unregister
            });
        }
    }

    public async Task SponsorAsync(AccountId sponsorable, AccountId sponsor, int amount, DateOnly? expiresAt = null, string? note = default)
    {
        var sponsorship = new Sponsorship(sponsorable.Id, sponsorable.Login, sponsor.Id, sponsor.Login, amount)
        {
            ExpiresAt = expiresAt
        };

        await SaveSponsorshipAndExpirationAsync(sponsorship);
        await events.PushAsync(new SponsorshipCreated(sponsorable.Id, sponsor.Id, amount, expiresAt, note));

        // NOTE: we persist above so a quick Enable/Disable of the admin app can successfully 
        // re-establish all current sponsors.
        await VerifySponsorableAsync(sponsorable);
        await UpdateSponsorRegistryAsync(sponsorable, sponsor);
    }

    public async Task SponsorUpdateAsync(AccountId sponsorable, AccountId sponsor, int amount, string? note = default)
    {
        var sponsorship = new Sponsorship(sponsorable.Id, sponsorable.Login, sponsor.Id, sponsor.Login, amount);

        // If there is an existing one, this should merge/update. 
        // Otherwise, this will create a new entry. This can be used to 
        // "refresh" an existing sponsor from before the app was installed.
        await SaveSponsorshipAndExpirationAsync(sponsorship);
        await events.PushAsync(new SponsorshipChanged(sponsorable.Id, sponsor.Id, amount, note));

        // NOTE: we persist above so a quick Enable/Disable of the admin app can successfully 
        // re-establish all current sponsors.
        await VerifySponsorableAsync(sponsorable);
        await UpdateSponsorRegistryAsync(sponsorable, sponsor);
    }

    public async Task UnsponsorAsync(AccountId sponsorable, AccountId sponsor, string? note = default)
    {
        var bySponsorable = TableRepository
            .Create<Sponsorship>(sponsorshipsConnection, x => x.SponsorableId, x => x.SponsorId);

        var sponsorship = await bySponsorable.GetAsync(sponsorable.Id, sponsor.Id);
        if (sponsorship == null)
            // TODO: We have no existing sponsorship to expire anyway, so ignore?
            return;

        var bySponsor = TableRepository
            .Create<Sponsorship>(sponsorshipsConnection, x => x.SponsorId, x => x.SponsorableId);

        sponsorship = await bySponsor.GetAsync(sponsor.Id, sponsorable.Id);
        if (sponsorship == null)
            // TODO: We have no existing sponsorship to expire anyway, so ignore?
            return;

        sponsorship = sponsorship with
        {
            Expired = true,
            ExpiresAt = DateOnly.FromDateTime(DateTime.Today),
        };

        await SaveSponsorshipAndExpirationAsync(sponsorship);
        await events.PushAsync(new SponsorshipCancelled(sponsorable.Id, sponsor.Id, note));

        // This means sponsorables need to be active to also *unregister* sponsors
        await VerifySponsorableAsync(sponsorable);

        await registry.UnregisterSponsorAsync(sponsorable, sponsor);
    }

    public async Task UnsponsorExpiredAsync(DateOnly date)
    {
        var expirations = TableRepository.Create(sponsorshipsConnection);

        await foreach (var expiration in expirations.EnumerateAsync($"Expiration-{date:O}"))
        {
            var ids = expiration.RowKey.Split('|');
            var sponsorship = await TablePartition
                .Create<Sponsorship>(sponsorshipsConnection, $"Sponsorable-{ids[0]}", x => x.SponsorId)
                .GetAsync(ids[1]);

            if (sponsorship != null)
                // Sets the expired flag, but does not refresh the expiration date column
                await SaveSponsorshipAsync(sponsorship with { Expired = true });

            await expirations.DeleteAsync(expiration);
        }
    }

    async Task VerifySponsorableAsync(AccountId sponsorable)
    {
        // We're always authorized to ourselves.
        if (sponsorable.Id == Constants.DevloopedId)
            return;

        var app = await FindAppAsync(AppKind.Sponsorable, sponsorable);
        if (app == null || app.State == AppState.Deleted)
            throw new ArgumentException($"No SponsorLink Admin installation found for {sponsorable.Login}.", nameof(sponsorable));

        if (app.State == AppState.Suspended)
            throw new ArgumentException($"SponsorLink Admin app was suspended by the {sponsorable.Login} account.", nameof(sponsorable));

        // Sponsorship from sponsorable > devlooped, which are required to use SponsorLink as a sponsorable.
        var sponsorship = await TablePartition.Create<Sponsorship>(sponsorshipsConnection, $"Sponsor-{sponsorable.Id}", x => x.SponsorId)
            .GetAsync(Constants.DevloopedId);

        if (sponsorship == null ||
            (sponsorship.ExpiresAt != null && sponsorship.ExpiresAt < DateOnly.FromDateTime(DateTime.UtcNow)))
            throw new ArgumentException($"SponsorLink usage requires an active sponsorship from {sponsorable.Login} to @{Constants.DevloopedLogin}.", nameof(sponsorable));
    }

    async Task SaveSponsorshipAndExpirationAsync(Sponsorship sponsorship)
    {
        var expirations = TableRepository.Create(sponsorshipsConnection);
        var sponsorables = TablePartition.Create<Sponsorship>(sponsorshipsConnection,
            $"Sponsorable-{sponsorship.SponsorableId}", x => x.SponsorId, Azure.Data.Tables.TableUpdateMode.Replace);

        // first retrieve existing, to see if there was a previous scheduled expiration 
        // from a prior one-time sponsorship that is being turned into a monthly one
        var existing = await sponsorables.GetAsync(sponsorship.SponsorId);
        if (existing?.ExpiresAt != null)
        {
            // Delete existing expiration record if present
            await expirations.DeleteAsync($"Expiration-{existing.ExpiresAt:O}", $"{sponsorship.SponsorableId}|{sponsorship.SponsorId}");
        }

        if (sponsorship.ExpiresAt != null)
        {
            // Schedule expiration for the daily check to pick up.
            await expirations.PutAsync(new($"Expiration-{sponsorship.ExpiresAt:O}", $"{sponsorship.SponsorableId}|{sponsorship.SponsorId}")
            {
                { "SponsorableLogin", sponsorship.SponsorableLogin },
                { "SponsorLogin", sponsorship.SponsorLogin },
            });
        }

        await SaveSponsorshipAsync(sponsorship);
    }

    async Task SaveSponsorshipAsync(Sponsorship sponsorship)
    {
        // Dual store for easier scanning
        var bySponsorable = TablePartition.Create<Sponsorship>(sponsorshipsConnection,
            $"Sponsorable-{sponsorship.SponsorableId}", x => x.SponsorId, Azure.Data.Tables.TableUpdateMode.Replace);
        var bySponsor = TablePartition.Create<Sponsorship>(sponsorshipsConnection,
            $"Sponsor-{sponsorship.SponsorId}", x => x.SponsorableId, Azure.Data.Tables.TableUpdateMode.Replace);

        // NOTE: we *replace* existing expiration if it was present, so we can potentially delete an existing expiration.
        await bySponsorable.PutAsync(sponsorship);
        await bySponsor.PutAsync(sponsorship);
    }

    /// <summary>
    /// Updates the registry in storage of the sponsors for the given sponsorable.
    /// </summary>
    /// <param name="sponsorable">The sponsorable account which must be a SL Admin user.</param>
    /// <param name="sponsor">The sponsoring user.</param>
    /// <param name="member">Whether the sponsor is a member of the sponsorable organization.</param>
    async Task<bool> UpdateSponsorRegistryAsync(AccountId sponsorable, AccountId sponsor, bool member = false)
    {
        var app = await FindAppAsync(AppKind.Sponsor, sponsor);
        if (app == null || app.State != AppState.Installed)
            // TODO: new sponsor but no app installed (or suspended)... should be fine?
            // This is basically a sponsor that doesn't necessarily need or use the library
            return false;

        var auth = await TablePartition.Create<Authorization>(tableConnection)
            .GetAsync(sponsor.Id);

        // Should be fine too if they cancelled somehow before auth completed?
        if (auth == null)
            return false;

        var emails = await new GitHubClient(octoProduct)
        {
            Credentials = new Credentials(auth.AccessToken)
        }.User.Email.GetAll();

        await registry.RegisterSponsorAsync(
            sponsorable, sponsor,
            emails.Where(x => x.Verified).Select(x => x.Email), 
            member);

        return true;
    }

    async Task<bool> UpdateAppRegistryAsync(AccountId account)
    {
        var app = await FindAppAsync(AppKind.Sponsor, account);
        if (app == null || app.State != AppState.Installed)
        {
            // In this case, we won't be able to retrieve users' emails since the 
            // installation is disabled or not uninstalled at all, so just unregister 
            // with all emails we had (if any)
            await registry.UnregisterAppAsync(account);
            return true;
        }

        var auth = await TablePartition.Create<Authorization>(tableConnection)
            .GetAsync(account.Id);

        // We'll need to retry until authorization works since we need the emails
        if (auth == null)
            return false;

        var client = new GitHubClient(octoProduct)
        {
            Credentials = new Credentials(auth.AccessToken)
        };

        var user = await client.User.Get(account.Login);
        var emails = await client.User.Email.GetAll();
        var verified = emails.Where(x => x.Verified).Select(x => x.Email).ToList();

        // All verified emails are required to associate sponsorship to local repo email
        await registry.RegisterAppAsync(account, verified);

        // For communication purposes, we just need the user's primary email address though.
        await accounts.PutAsync(new Account(account.Id, account.Login, user.Email));

        return true;
    }

    async Task ChangeState(AppKind kind, AccountId account, AppState state)
    {
        var partition = TablePartition.Create<Installation>(tableConnection,
            kind == AppKind.Sponsorable ? "Sponsorable" : "Sponsor");

        var installed = await partition.GetAsync(account.Id);
        if (installed == null)
            throw new ArgumentException($"{kind} app is not installed for account {account.Login}.");

        await partition.PutAsync(installed with { State = state });
    }
}
