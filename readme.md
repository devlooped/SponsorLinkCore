# SponsorLink

Required web app settings:

* GitHub:WebhookSecret = [a shared secret initially configured for the webhook of both sponsorlink and admin apps]
* GitHub:Sponsorable:RedirectUri = https://[HOST]/authorize/sponsorable
* GitHub:Sponsorable:ClientSecret =  [from https://github.com/organizations/devlooped/settings/apps/sponsorlink-admin]
* GitHub:Sponsorable:ClientId =  [from https://github.com/organizations/devlooped/settings/apps/sponsorlink-admin]
* GitHub:Sponsorable:AppId =  [from https://github.com/organizations/devlooped/settings/apps/sponsorlink-admin]
* GitHub:Sponsor:RedirectUri = https://[HOST]/authorize/sponsor
* GitHub:Sponsor:ClientSecret = [from https://github.com/organizations/devlooped/settings/apps/sponsorlink]
* GitHub:Sponsor:ClientId = [from https://github.com/organizations/devlooped/settings/apps/sponsorlink]
* GitHub:Sponsor:AppId = [from https://github.com/organizations/devlooped/settings/apps/sponsorlink]
* EventGrid:Domain = [from Event Grid Domain overview]
* EventGrid:AccessKey = [from Event Grid Domain access keys]


Devlooped org needs to have the [SponsorLink Admin](https://github.com/apps/sponsorlink-admin) app 
installed. After the installation, find the Sponsorable record in storage for the devlooped organization 
and make sure the `Secret` column has the same value as the `GitHub:WebhookSecret`. Both should always be in sync. 
If the admin app is ever re-installed, the webhook secret will need updating too (or the Secret column).