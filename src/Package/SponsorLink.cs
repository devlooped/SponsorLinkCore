﻿using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Devlooped;

/// <summary>
/// Gathers and exposes sponsor link attribution for a given sponsorable 
/// account that consumes SponsorLink. 
/// </summary>
/// <remarks>
/// Intended usage is for the library author to add a Roslyn source 
/// generator to the package, and initialize a new instance of this class 
/// with both the sponsorable account and the product name to use for the checks.
/// </remarks>
public class SponsorLink
{
    static readonly HttpClient http = new();
    static readonly Random rnd = new();

    static readonly DiagnosticDescriptor broken = SponsorLinkAnalyzer.CreateBroken(DescriptorKind.Generator);
    static readonly DiagnosticDescriptor appNotInstalled = SponsorLinkAnalyzer.CreateAppNotInstalled(DescriptorKind.Generator);
    static readonly DiagnosticDescriptor userNotSponsoring = SponsorLinkAnalyzer.CreateUserNotSponsoring(DescriptorKind.Generator);

    readonly string sponsorable;
    readonly Action<SourceProductionContext, BuildInfo> notInstalled;
    readonly Action<SourceProductionContext, BuildInfo> nonSponsor;
    readonly Action<SourceProductionContext, BuildInfo> activeSponsor;
    
    /// <summary>
    /// Creates the sponsor link instance for the given sponsorable account, used to 
    /// check for active installation and sponsorships for the current user (given 
    /// their configured git email).
    /// </summary>
    /// <param name="sponsorable">A sponsorable account that has been properly provisioned with SponsorLink.</param>
    /// <param name="product">The product developed by <paramref name="sponsorable"/> that is checking the sponsorship link.</param>
    public SponsorLink(string sponsorable, string product)
        : this(sponsorable, product, 0, 4000) { }

    /// <summary>
    /// Creates the sponsor link instance for the given sponsorable account, used to 
    /// check for active installation and sponsorships for the current user (given 
    /// their configured git email).
    /// </summary>
    /// <param name="sponsorable">A sponsorable account that has been properly provisioned with SponsorLink.</param>
    /// <param name="product">The product developed by <paramref name="sponsorable"/> that is checking the sponsorship link.</param>
    /// <param name="pauseMin">Min random milliseconds to apply during build for non-sponsored users. Use 0 for no pause.</param>
    /// <param name="pauseMax">Max random milliseconds to apply during build for non-sponsored users. Use 0 for no pause.</param>
    public SponsorLink(string sponsorable, string product, int pauseMin, int pauseMax)
        : this(sponsorable,
              (context, info) =>
              {
                  // Add a random configurable pause in this case.
                  var pause = rnd.Next(pauseMin, pauseMax);
                  var diag = Diagnostic.Create(appNotInstalled, 
                      Location.Create(info.ProjectPath, new TextSpan(0, 0), new LinePositionSpan()),
                      product, sponsorable, pause);

                  // If it was supressed, hidden or changed, report it here. This check 
                  // avoids reporting twice.
                  if (SponsorLinkAnalyzer.AppNotInstalled.GetEffectiveSeverity(info.CompilationOptions!) != ReportDiagnostic.Warn)
                      context.ReportDiagnostic(diag);
                  
                  Thread.Sleep(pause);
                  WriteMessage(sponsorable, product, Path.GetDirectoryName(info.ProjectPath), diag);
              },
              (context, info) =>
              {
                  // Add a random configurable pause in this case.
                  var pause = rnd.Next(pauseMin, pauseMax);
                  var diag = Diagnostic.Create(userNotSponsoring,
                      Location.Create(info.ProjectPath, new TextSpan(0, 0), new LinePositionSpan()),
                      product, sponsorable, pause);

                  // If it was supressed, hidden or changed, report it here. This check 
                  // avoids reporting twice.
                  if (SponsorLinkAnalyzer.AppNotInstalled.GetEffectiveSeverity(info.CompilationOptions!) != ReportDiagnostic.Warn)
                      context.ReportDiagnostic(diag);

                  Thread.Sleep(pause);
                  WriteMessage(sponsorable, product, Path.GetDirectoryName(info.ProjectPath), diag);
              },
              (context, info) =>
              {
                  // NOTE: reporting the Info diagnostics from the source generator results 
                  // in it NEVER being shown for users, regardless of the Location we use.
                  // So we instead skip it here and let the analyzer report it by discovering 
                  // the written files.
                  WriteMessage(sponsorable, product, Path.GetDirectoryName(info.ProjectPath), Diagnostic.Create(
                      SponsorLinkAnalyzer.Thanks, null,
                      product, sponsorable));
              })
    { }

    static void WriteMessage(string sponsorable, string product, string projectDir, Diagnostic diag)
    {
        var objDir = Path.Combine(projectDir, "obj", "SponsorLink", sponsorable, product);
        if (Directory.Exists(objDir))
        {
            foreach (var file in Directory.EnumerateFiles(objDir))
                File.Delete(file);
        }

        Directory.CreateDirectory(objDir);
        // We always write the analyzer-based IDs.
        File.WriteAllText(Path.Combine(objDir, $"{diag.Id.Replace("SLI", "SL")}.{diag.Severity}.txt"), diag.GetMessage());
    }

    /// <summary>
    /// Advanced overload that allows granular behavior customization for the sponsorable account.
    /// </summary>
    /// <param name="sponsorable">A sponsorable account that has been properly provisioned with SponsorLink.</param>
    /// <param name="notInstalled">Action to invoke when the user has not installed the SponsorLink app yet (or has disabled it).</param>
    /// <param name="nonSponsor">Action to invoke when the user has installed the app but is not sponsoring.</param>
    /// <param name="activeSponsor">Action to invoke when the user has installed the app and is sponsoring the account.</param>
    /// <remarks>
    /// The action delegates receive the generator context and the current project path.
    /// </remarks>
    public SponsorLink(string sponsorable, 
        Action<SourceProductionContext, BuildInfo> notInstalled, 
        Action<SourceProductionContext, BuildInfo> nonSponsor, 
        Action<SourceProductionContext, BuildInfo> activeSponsor)
    {
        this.sponsorable = sponsorable;
        this.notInstalled = notInstalled;
        this.nonSponsor = nonSponsor;
        this.activeSponsor = activeSponsor;
    }

    /// <summary>
    /// Initializes the sponsor link checks during builds.
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var dirs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(x =>
                x.Right.GetOptions(x.Left).TryGetValue("build_metadata.AdditionalFiles.SourceItemType", out var itemType) &&
                itemType == "MSBuildProject")
            .Select((x, c) =>
            {
                var opt = x.Right.GlobalOptions;
                var insideEditor =
                    !opt.TryGetValue("build_property.BuildingInsideVisualStudio", out var value) ||
                    !bool.TryParse(value, out var bv) ? null : (bool?)bv;

                // Override value if we detect R#/Rider in use.
                if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k =>
                        k.StartsWith("RESHARPER") ||
                        k.StartsWith("IDEA_")))
                    insideEditor = true;

                var dtb =
                    !opt.TryGetValue("build_property.DesignTimeBuild", out value) ||
                    !bool.TryParse(value, out bv) ? null : (bool?)bv;

                return new BuildInfo(x.Left.Path, insideEditor, dtb);
            })
            .Combine(context.CompilationProvider)
            .Select((x, c) => x.Left.WithCompilationOptions(x.Right.Options));

        context.RegisterSourceOutput(dirs.Collect(), CheckSponsor);
    }

    void CheckSponsor(SourceProductionContext context, ImmutableArray<BuildInfo> states)
    {
        if (states.IsDefaultOrEmpty || states[0].InsideEditor == null)
        {
            // Broken state
            context.ReportDiagnostic(Diagnostic.Create(broken, null));
            return;
        }

        var state = states[0];

        // We never pause in DTB
        if (state.DesignTimeBuild == true)
            return;

        // We never pause in non-IDE builds
        if (state.InsideEditor == false)
            return;

        // If there is no network at all, don't do anything.
        if (!NetworkInterface.GetIsNetworkAvailable())
            return;

        var email = GetEmail(Path.GetDirectoryName(state.ProjectPath));
        // No email configured in git. Weird.
        if (string.IsNullOrEmpty(email))
            return;

        // Check app install and sponsoring status
        var installed = UrlExists($"https://devlooped.blob.core.windows.net/sponsorlink/apps/{email}", context.CancellationToken);
        var sponsoring = UrlExists($"https://devlooped.blob.core.windows.net/sponsorlink/{sponsorable}/{email}", context.CancellationToken);

        // Faulted HTTP HEAD request checking for url?
        if (installed == null || sponsoring == null)
            return;

        if (installed == false)
            notInstalled(context, state);
        else if (sponsoring == false)
            nonSponsor(context, state);
        else
            activeSponsor(context, state);
    }

    static string? GetEmail(string workingDirectory)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo("git", "config --get user.email")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            });
            proc.WaitForExit();

            // Couldn't run git config, so we can't check for sponsorship, no email to check.
            if (proc.ExitCode != 0)
                return null;

            return proc.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            // Git not even installed.
        }

        return null;
    }

    static bool? UrlExists(string url, CancellationToken cancellation)
    {
        var ev = new ManualResetEventSlim();
        bool? exists = null;
        http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cancellation)
            .ContinueWith(t =>
            {
                if (!t.IsFaulted)
                    exists = t.IsCompleted && t.Result.IsSuccessStatusCode;

                ev.Set();
            });

        ev.Wait(cancellation);
        return exists;
    }

    /// <summary>
    /// Provides information about the build that was checked for sponsor linking.
    /// </summary>
    public class BuildInfo
    {
        internal BuildInfo(string path, bool? insideEditor, bool? designTimeBuild)
        {
            ProjectPath = path;
            InsideEditor = insideEditor;
            DesignTimeBuild = designTimeBuild;
        }

        /// <summary>
        /// The full path of the project being built.
        /// </summary>
        public string ProjectPath { get; }
        /// <summary>
        /// Whether the build is happening inside an editor.
        /// </summary>
        public bool? InsideEditor { get; }
        /// <summary>
        /// Whether the build is a design-time build.
        /// </summary>
        public bool? DesignTimeBuild { get; }
        /// <summary>
        /// Compilation options being used for the build.
        /// </summary>
        public CompilationOptions? CompilationOptions { get; private set; }

        internal BuildInfo WithCompilationOptions(CompilationOptions options)
        {
            CompilationOptions = options;
            return this;
        }
    }
}
