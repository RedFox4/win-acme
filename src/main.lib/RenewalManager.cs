﻿using Autofac;
using Autofac.Core;
using PKISharp.WACS.Clients.IIS;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Base.Options;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Plugins.TargetPlugins;
using PKISharp.WACS.Services;
using PKISharp.WACS.Services.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PKISharp.WACS
{
    internal class RenewalManager
    {
        private readonly IInputService _input;
        private readonly ILogService _log;
        private readonly IPluginService _plugin;
        private readonly IRenewalStore _renewalStore;
        private readonly ICacheService _cacheService;
        private readonly ArgumentsParser _arguments;
        private readonly MainArguments _args;
        private readonly ISharingLifetimeScope _container;
        private readonly IAutofacBuilder _scopeBuilder;
        private readonly ExceptionHandler _exceptionHandler;
        private readonly RenewalExecutor _renewalExecutor;
        private readonly RenewalCreator _renewalCreator;
        private readonly ISettingsService _settings;
        private readonly IDueDateService _dueDate;

        public RenewalManager(
            ArgumentsParser arguments, MainArguments args,
            IRenewalStore renewalStore, ISharingLifetimeScope container,
            ICacheService cacheService, IPluginService plugin,
            IInputService input, ILogService log,
            ISettingsService settings, IDueDateService dueDate,
            IAutofacBuilder autofacBuilder, ExceptionHandler exceptionHandler,
            RenewalCreator renewalCreator,
            RenewalExecutor renewalExecutor)
        {
            _renewalStore = renewalStore;
            _args = args;
            _input = input;
            _log = log;
            _settings = settings;
            _arguments = arguments;
            _container = container;
            _scopeBuilder = autofacBuilder;
            _exceptionHandler = exceptionHandler;
            _renewalExecutor = renewalExecutor;
            _renewalCreator = renewalCreator;
            _cacheService = cacheService;
            _dueDate = dueDate;
            _plugin = plugin;
        }

        /// <summary>
        /// Renewal management mode
        /// </summary>
        /// <returns></returns>
        internal async Task ManageRenewals()
        {
            IEnumerable<Renewal> originalSelection = _renewalStore.Renewals.OrderBy(x => x.LastFriendlyName);
            var selectedRenewals = originalSelection;
            var quit = false;
            var displayAll = false;
            do
            {
                var all = selectedRenewals.Count() == originalSelection.Count();
                var none = !selectedRenewals.Any();
                var totalLabel = originalSelection.Count() != 1 ? "renewals" : "renewal";
                var renewalSelectedLabel = selectedRenewals.Count() != 1 ? "renewals" : "renewal";
                var selectionLabel = 
                    all ? selectedRenewals.Count() == 1 ? "the renewal" : "*all* renewals" : 
                    none ? "no renewals" :  
                    $"{selectedRenewals.Count()} of {originalSelection.Count()} {totalLabel}";

                _input.CreateSpace();
                _input.Show(null, 
                    "Welcome to the renewal manager. Actions selected in the menu below will " +
                    "be applied to the following list of renewals. You may filter the list to target " +
                    "your action at a more specific set of renewals, or sort it to make it easier to " +
                    "find what you're looking for.");

                var displayRenewals = selectedRenewals;
                var displayLimited = !displayAll && selectedRenewals.Count() >= _settings.UI.PageSize;
                var displayHidden = 0;
                var displayHiddenLabel = "";
                if (displayLimited)
                {
                    displayRenewals = displayRenewals.Take(_settings.UI.PageSize - 1);
                    displayHidden = selectedRenewals.Count() - displayRenewals.Count();
                    displayHiddenLabel = displayHidden != 1 ? "renewals" : "renewal";
                }
                var choices = displayRenewals.Select(x => Choice.Create<Renewal?>(x,
                                  description: x.ToString(_dueDate, _input),
                                  color: x.History.LastOrDefault()?.Success ?? false ?
                                          _dueDate.IsDue(x) ?
                                              ConsoleColor.DarkYellow :
                                              ConsoleColor.Green :
                                          ConsoleColor.Red)).ToList();
                if (displayLimited)
                {
                    choices.Add(Choice.Create<Renewal?>(null,
                                  command: "More",
                                  description: $"{displayHidden} additional {displayHiddenLabel} selected but currently not displayed"));
                }
                await _input.WritePagedList(choices);
                displayAll = false;

                var noneState = none ? State.DisabledState("No renewals selected.") : State.EnabledState();
                var sortFilterState = selectedRenewals.Count() < 2 ? State.DisabledState("Not enough renewals to sort/filter.") : State.EnabledState(); ;
                var editState =
                    selectedRenewals.Count() != 1 
                        ? none 
                            ? State.DisabledState("No renewals selected.") 
                            : State.DisabledState("Multiple renewals selected.") 
                        : State.EnabledState();

                var options = new List<Choice<Func<Task>>>();
                if (displayLimited)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            () => { displayAll = true; return Task.CompletedTask; },
                            "List all selected renewals", "A"));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => { quit = true; await EditRenewal(selectedRenewals.First()); },
                        "Edit renewal", "E", state: editState));
                if (selectedRenewals.Count() > 1)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            async () => selectedRenewals = await FilterRenewalsMenu(selectedRenewals),
                            all ? "Apply filter" : "Apply additional filter", "F", state: sortFilterState));
                    options.Add(
                        Choice.Create<Func<Task>>(
                             async () => selectedRenewals = await SortRenewalsMenu(selectedRenewals),
                            "Sort renewals", "S", state: sortFilterState));
                }
                if (!all)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            () => { selectedRenewals = originalSelection; return Task.CompletedTask; },
                            "Reset sorting and filtering", "X"));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => { 
                            foreach (var renewal in selectedRenewals) {
                                var index = selectedRenewals.ToList().IndexOf(renewal) + 1;
                                _input.Show($"{index}/{selectedRenewals.Count()}");
                                await ShowRenewal(renewal);
                                var cont = false;
                                if (index != selectedRenewals.Count())
                                {
                                    cont = await _input.Wait("Press <Enter> to continue or <Esc> to abort");
                                    if (!cont)
                                    {
                                        break;
                                    }
                                } 
                                else
                                {
                                    await _input.Wait();
                                }

                            } 
                        },
                        $"Show details for {selectionLabel}", "D",
                        state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            foreach (var renewal in selectedRenewals)
                            {
                                _input.Show(null, AsCommandLine(renewal));
                                _input.CreateSpace();
                            }
                            await _input.Wait();
                        },
                        $"Show command line for {selectionLabel}", "L",
                        state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive),
                        $"Run {selectionLabel}", "R", state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive | RunLevel.Force),
                        $"Run {selectionLabel} (force)", "S", state: noneState));
                if (_settings.Cache.ReuseDays > 0)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive | RunLevel.Force | RunLevel.NoCache),
                        $"Run {selectionLabel} (force, no cache)", "T", state: noneState));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => selectedRenewals = await Analyze(selectedRenewals),
                        $"Analyze duplicates for {selectionLabel}", "U", state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            var confirm = await _input.PromptYesNo($"Are you sure you want to cancel {selectedRenewals.Count()} currently selected {renewalSelectedLabel}?", false);
                            if (confirm)
                            {
                                foreach (var renewal in selectedRenewals)
                                {
                                    _renewalStore.Cancel(renewal);
                                    _cacheService.Delete(renewal);
                                };
                                originalSelection = _renewalStore.Renewals.OrderBy(x => x.LastFriendlyName);
                                selectedRenewals = originalSelection;
                            }
                        },
                        $"Cancel {selectionLabel}", "C", state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            var confirm = await _input.PromptYesNo($"Are you sure you want to revoke the most recently issued certificate for {selectedRenewals.Count()} currently selected {renewalSelectedLabel}? This should only be done in case of a (suspected) security breach. Cancel the {renewalSelectedLabel} if you simply don't need the certificates anymore.", false);
                            if (confirm)
                            {
                                await RevokeCertificates(selectedRenewals);
                            }
                        },
                        $"Revoke certificate(s) for {selectionLabel}", "V", state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        () => { quit = true; return Task.CompletedTask; },
                        "Back", "Q",
                        @default: !originalSelection.Any()));

                if (selectedRenewals.Count() > 1)
                {
                    _input.CreateSpace();
                    _input.Show(null, $"Currently selected {selectedRenewals.Count()} of {originalSelection.Count()} {totalLabel}");
                }
                var chosen = await _input.ChooseFromMenu(
                    "Choose an action or type numbers to select renewals",
                    options, 
                    (string unexpected) =>
                        Choice.Create<Func<Task>>(() => { selectedRenewals = FilterRenewalsById(selectedRenewals, unexpected); return Task.CompletedTask; }));
                await chosen.Invoke();
                _container.Resolve<IIISClient>().Refresh();
            }
            while (!quit);
        }

        /// <summary>
        /// Run selected renewals
        /// </summary>
        /// <param name="selectedRenewals"></param>
        /// <param name="flags"></param>
        /// <returns></returns>
        private async Task Run(IEnumerable<Renewal> selectedRenewals, RunLevel flags)
        {
            WarnAboutRenewalArguments();
            foreach (var renewal in selectedRenewals)
            {
                await ProcessRenewal(renewal, flags);
            }
        }

        /// <summary>
        /// Helper to get target for a renewal
        /// </summary>
        /// <param name="renewal"></param>
        /// <returns></returns>
        private async Task<Target?> GetTarget(Renewal renewal)
        {
            try
            {
                using var targetScope = _scopeBuilder.PluginBackend<ITargetPlugin, TargetPluginOptions>(_container, renewal.TargetPluginOptions);
                var targetBackend = targetScope.Resolve<ITargetPlugin>();
                return await targetBackend.Generate();
            } 
            catch
            {
                
            }
            return null;
        }

        /// <summary>
        /// Check if there are multiple renewals installing to the same site 
        /// or requesting certificates for the same domains
        /// </summary>
        /// <param name="selectedRenewals"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> Analyze(IEnumerable<Renewal> selectedRenewals)
        {
            var foundHosts = new Dictionary<Identifier, List<Renewal>>();
            var foundSites = new Dictionary<long, List<Renewal>>();

            foreach (var renewal in selectedRenewals)
            {
                var initialTarget = await GetTarget(renewal);
                if (initialTarget == null)
                {
                    _log.Warning("Unable to generate source for renewal {renewal}, analysis incomplete", renewal.FriendlyName);
                    continue;
                }
                foreach (var targetPart in initialTarget.Parts)
                {
                    if (targetPart.SiteId != null)
                    {
                        var siteId = targetPart.SiteId.Value;
                        if (!foundSites.ContainsKey(siteId))
                        {
                            foundSites.Add(siteId, new List<Renewal>());
                        }
                        foundSites[siteId].Add(renewal);
                    }
                    foreach (var host in targetPart.GetIdentifiers(true))
                    {
                        if (!foundHosts.ContainsKey(host))
                        {
                            foundHosts.Add(host, new List<Renewal>());
                        }
                        foundHosts[host].Add(renewal);
                    }
                }
            }

            // List results
            var options = new List<Choice<List<Renewal>>>();
            foreach (var site in foundSites)
            {
                if (site.Value.Count > 1)
                {
                    options.Add(
                      Choice.Create(
                          site.Value,
                          $"Select {site.Value.Count} renewals covering IIS site {site.Key}"));
                }
            }
            foreach (var host in foundHosts)
            {
                if (host.Value.Count > 1)
                {
                    options.Add(
                      Choice.Create(
                          host.Value,
                          $"Select {host.Value.Count} renewals covering host {host.Key}"));
                }
            }
            _input.CreateSpace();
            if (options.Count == 0)
            {
                _input.Show(null, "Analysis didn't find any overlap between renewals.");
                return selectedRenewals;
            }
            else
            {
                options.Add(
                    Choice.Create(
                        selectedRenewals.ToList(),
                        $"Back"));
                _input.Show(null, "Analysis found some overlap between renewals. You can select the overlapping renewals from the menu.");
                return await _input.ChooseFromMenu("Please choose from the menu", options);
            }
        }

        /// <summary>
        /// Offer user different ways to sort the renewals
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> SortRenewalsMenu(IEnumerable<Renewal> current)
        {
            var options = new List<Choice<Func<IEnumerable<Renewal>>>>
            {
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderBy(x => x.LastFriendlyName),
                    "Sort by friendly name",
                    @default: true),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderByDescending(x => x.LastFriendlyName),
                    "Sort by friendly name (descending)"),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderBy(x => _dueDate.DueDate(x)),
                    "Sort by due date"),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderByDescending(x => _dueDate.DueDate(x)),
                    "Sort by due date (descending)")
            };
            var chosen = await _input.ChooseFromMenu("How would you like to sort the renewals list?", options);
            return chosen.Invoke();
        }

        /// <summary>
        /// Offer user different ways to filter the renewals
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> FilterRenewalsMenu(IEnumerable<Renewal> current)
        {
            var options = new List<Choice<Func<Task<IEnumerable<Renewal>>>>>
            {
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => FilterRenewalsByFriendlyName(current),
                    "Filter by friendly name"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => _dueDate.IsDue(x))),
                    "Filter by due status (keep due)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => !_dueDate.IsDue(x))),
                    "Filter by due status (remove due)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => x.History.Last().Success != true)),
                    "Filter by error status (keep errors)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => x.History.Last().Success == true)),
                    "Filter by error status (remove errors)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current),
                    "Cancel")
            };
            var chosen = await _input.ChooseFromMenu("How would you like to filter?", options);
            return await chosen.Invoke();
        }

        private IEnumerable<Renewal> FilterRenewalsById(IEnumerable<Renewal> current, string input)
        {
            var parts = input.ParseCsv();
            if (parts == null)
            {
                return current;
            }
            var ret = new List<Renewal>();
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var index))
                {
                    if (index > 0 && index <= current.Count())
                    {
                        ret.Add(current.ElementAt(index - 1));
                    }
                    else
                    {
                        _log.Warning("Input out of range: {part}", part);
                    }
                }
                else
                {
                    _log.Warning("Invalid input: {part}", part);
                }
            }
            return ret;
        }

        /// <summary>
        /// Filter specific renewals by friendly name
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> FilterRenewalsByFriendlyName(IEnumerable<Renewal> current)
        {
            _input.CreateSpace();
            _input.Show(null, "Please input friendly name to filter renewals by. " + IISArguments.PatternExamples);
            var rawInput = await _input.RequestString("Friendly name");
            var ret = new List<Renewal>();
            var regex = new Regex(rawInput.PatternToRegex(), RegexOptions.IgnoreCase);
            foreach (var r in current)
            {
                if (!string.IsNullOrEmpty(r.LastFriendlyName) && regex.Match(r.LastFriendlyName).Success)
                {
                    ret.Add(r);
                }
            }
            return ret;
        }

        /// <summary>
        /// Filters for unattended mode
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        private IEnumerable<Renewal> FilterRenewalsByCommandLine(string command)
        {
            if (_args.HasFilter)
            {
                var targets = _renewalStore.FindByArguments(
                    _args.Id,
                    _args.FriendlyName);
                if (!targets.Any())
                {
                    _log.Error("No renewals matched.");
                }
                return targets;
            }
            else
            {
                _log.Error($"Specify which renewal to {command} using the parameter --id or --friendlyname.");
            }
            return new List<Renewal>();
        }

        /// <summary>
        /// Loop through the store renewals and run those which are
        /// due to be run
        /// </summary>
        internal async Task CheckRenewals(RunLevel runLevel)
        {
            IEnumerable<Renewal> renewals;
            if (_args.HasFilter)
            {
                renewals = _renewalStore.FindByArguments(_args.Id, _args.FriendlyName);
                if (!renewals.Any())
                {
                    _log.Error("No renewals found that match the filter parameters --id and/or --friendlyname.");
                }
            }
            else
            {
                _log.Verbose("Checking renewals");
                renewals = _renewalStore.Renewals;
                if (!renewals.Any())
                {
                    _log.Warning("No scheduled renewals found.");
                }
            }

            if (renewals.Any())
            {
                WarnAboutRenewalArguments();
                foreach (var renewal in renewals)
                {
                    try
                    {
                        var success = await ProcessRenewal(renewal, runLevel);
                        if (success == false)
                        {
                            // Make sure the ExitCode is set
                            _exceptionHandler.HandleException();
                        }
                    } 
                    catch (Exception ex)
                    {
                        _exceptionHandler.HandleException(ex, "Unhandled error processing renewal");
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Process a single renewal
        /// </summary>
        /// <param name="renewal"></param>
        internal async Task<bool?> ProcessRenewal(Renewal renewal, RunLevel runLevel)
        {
            var notification = _container.Resolve<NotificationService>();
            try
            {
                var result = await _renewalExecutor.HandleRenewal(renewal, runLevel);
                if (!result.Abort)
                {
                    _renewalStore.Save(renewal, result);
                    if (result.Success == true)
                    {
                        await notification.NotifySuccess(renewal, _log.Lines);
                        return true;
                    }
                    else
                    {
                        await notification.NotifyFailure(runLevel, renewal, result, _log.Lines);
                        return false;
                    }
                } 
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex);
                await notification.NotifyFailure(runLevel, renewal, new RenewResult(ex.Message), _log.Lines);
                return false;
            }
        }

        /// <summary>
        /// Show a warning when the user appears to be trying to
        /// use command line arguments in combination with a renew
        /// command.
        /// </summary>
        internal void WarnAboutRenewalArguments()
        {
            if (_arguments.Active())
            {
                _log.Warning("You have specified command line options for plugins. " +
                    "Note that these only affect new certificates, but NOT existing renewals. " +
                    "To change settings, re-create (overwrite) the renewal.");
            }
        }

        /// <summary>
        /// "Edit" renewal
        /// </summary>
        private async Task EditRenewal(Renewal renewal)
        {
            var options = new List<Choice<Steps>>
            {
                Choice.Create(Steps.All, "All"),
                Choice.Create(Steps.Source, "Source"),
                Choice.Create(Steps.Order, "Order"),
                Choice.Create(Steps.Csr, "CSR"),
                Choice.Create(Steps.Validation, "Validation"),
                Choice.Create(Steps.Store, "Store"),
                Choice.Create(Steps.Installation, "Installation"),
                Choice.Create(Steps.None, "Cancel")
            };
            var chosen = await _input.ChooseFromMenu("Which step do you want to edit?", options);
            if (chosen != Steps.None)
            {
                await _renewalCreator.SetupRenewal(RunLevel.Interactive | RunLevel.Advanced | RunLevel.Force, chosen, renewal);
            }
        }

        /// <summary>
        /// Show certificate details
        /// </summary>
        private async Task ShowRenewal(Renewal renewal)
        {
            try
            {
                _input.CreateSpace();
                _input.Show("Id", renewal.Id);
                _input.Show("File", $"{renewal.Id}.renewal.json");
                _input.Show("FriendlyName", string.IsNullOrEmpty(renewal.FriendlyName) ? $"[Auto] {renewal.LastFriendlyName}" : renewal.FriendlyName);
                _input.Show(".pfx password", renewal.PfxPassword?.Value);
                var expires = renewal.History.Where(x => x.Success == true).LastOrDefault()?.ExpireDate;
                if (expires == null)
                {
                    _input.Show("Expires", "Unknown");
                }
                else
                {
                    _input.Show("Expires", _input.FormatDate(expires.Value));
                }
                var dueDate = _dueDate.DueDate(renewal);
                if (dueDate == null)
                {
                    _input.Show("Renewal due", "Now");
                } 
                else
                {
                    _input.Show("Renewal due", _input.FormatDate(dueDate.Value));
                }
                _input.Show("Renewed", $"{renewal.History.Where(x => x.Success == true).Count()} times");
                _input.Show("Command", AsCommandLine(renewal));
                _input.CreateSpace();
                renewal.TargetPluginOptions.Show(_input, _plugin);
                renewal.ValidationPluginOptions.Show(_input, _plugin);
                renewal.OrderPluginOptions?.Show(_input, _plugin);
                renewal.CsrPluginOptions?.Show(_input, _plugin);
                foreach (var ipo in renewal.StorePluginOptions)
                {
                    ipo.Show(_input, _plugin);
                }
                foreach (var ipo in renewal.InstallationPluginOptions)
                {
                    ipo.Show(_input, _plugin);
                }
                _input.CreateSpace();
                // Show history
                var historyLimit = 10; 
                await _input.WritePagedList(
                    renewal.History.
                    AsEnumerable().
                    Reverse().
                    Take(historyLimit).
                    Select(x => Choice.Create(x, command: "")));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Unable to list details for target");
            }
        }

        /// <summary>
        /// Write the command line that can be used to create 
        /// </summary>
        /// <param name="renewal"></param>
        private string AsCommandLine(Renewal renewal)
        {
            // Show the command line that may be used to (re)create this renewal
            var args = new Dictionary<string, string?>();
            var addArgs = (PluginOptions p) =>
            {
                var arguments = p.Describe(_container, _scopeBuilder, _plugin);
                foreach (var arg in arguments)
                {
                    var meta = arg.Key;
                    if (!args.ContainsKey(meta.ArgumentName))
                    {
                        var value = arg.Value;
                        if (value != null)
                        {
                            var add = true;
                            if (value is ProtectedString protectedString)
                            {
                                value = protectedString.Value?.StartsWith(SecretServiceManager.VaultPrefix) ?? false ? protectedString.Value : (object)"*******";
                            }
                            else if (value is string singleString)
                            {
                                value = meta.Secret ? "*******" : Escape(singleString); 
                            } 
                            else if (value is List<string> listString)
                            {
                                value = Escape(string.Join(",", listString));
                            }
                            else if (value is List<int> listInt)
                            {
                                value = string.Join(",", listInt);
                            }
                            else if (value is List<long> listLong)
                            {
                                value = string.Join(",", listLong);
                            }
                            else if (value is bool boolean)
                            {
                                value = boolean ? null : add = false;
                            }
                            if (add)
                            {
                                args.Add(meta.ArgumentName, value?.ToString());
                            }
                        }
                    }
                }
            };

            args.Add("source", _plugin.GetPlugin(renewal.TargetPluginOptions).Name.ToLower());
            addArgs(renewal.TargetPluginOptions);
            var validationPlugin = _plugin.GetPlugin(renewal.ValidationPluginOptions);
            var validationName = validationPlugin.Name.ToLower();
            if (!string.Equals(validationName, _settings.Validation.DefaultValidation ?? "selfhosting", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("validation", validationName);
            }
            addArgs(renewal.ValidationPluginOptions);
            if (renewal.OrderPluginOptions != null)
            {
                var orderName = _plugin.GetPlugin(renewal.OrderPluginOptions).Name.ToLower();
                if (!string.Equals(orderName, _settings.Order.DefaultPlugin ?? "single", StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("order", orderName);
                }
                addArgs(renewal.OrderPluginOptions);
            }
            if (renewal.CsrPluginOptions != null)
            {
                var csrName = _plugin.GetPlugin(renewal.CsrPluginOptions).Name.ToLower();
                if (!string.Equals(csrName, _settings.Csr.DefaultCsr ?? "rsa", StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("csr", csrName);
                }
                addArgs(renewal.CsrPluginOptions);
            }
            var storeNames = string.Join(",", renewal.StorePluginOptions.Select(_plugin.GetPlugin).Select(x => x.Name.ToLower()));
            if (!string.Equals(storeNames, _settings.Store.DefaultStore ?? "certificatestore", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("store", storeNames);
            }
            foreach (var so in renewal.StorePluginOptions)
            {
                addArgs(so);
            }
            var installationNames = string.Join(",", renewal.InstallationPluginOptions.Select(_plugin.GetPlugin).Select(x => x.Name.ToLower()));
            if (!string.Equals(installationNames, _settings.Installation.DefaultInstallation ?? "none", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("installation", installationNames);
            }
            foreach (var so in renewal.InstallationPluginOptions)
            {
                addArgs(so);
            }
            return "wacs.exe " + string.Join(" ", args.Select(a => $"--{a.Key.ToLower()} {a.Value}".Trim()));
        }

        private static string Escape(string value)
        {
            if (value.Contains(' ') || value.Contains('"'))
            {
                return $"\"{value.Replace("\"", "\\\"")}\"";
            }
            return value;
        }

        #region  Unattended 

        /// <summary>
        /// For command line --list
        /// </summary>
        /// <returns></returns>
        internal async Task ShowRenewalsUnattended()
        {
            await _input.WritePagedList(
                 _renewalStore.Renewals.Select(x => Choice.Create<Renewal?>(x,
                    description: x.ToString(_dueDate, _input),
                    color: x.History.Last().Success == true ?
                            _dueDate.IsDue(x) ?
                                ConsoleColor.DarkYellow :
                                ConsoleColor.Green :
                            ConsoleColor.Red)));
        }

        /// <summary>
        /// Cancel certificate from the command line
        /// </summary>
        /// <returns></returns>
        internal void CancelRenewalsUnattended()
        {
            var targets = FilterRenewalsByCommandLine("cancel");
            foreach (var t in targets)
            {
                _renewalStore.Cancel(t);
                _cacheService.Delete(t);
            }
        }

        /// <summary>
        /// Revoke certifcate from the command line
        /// </summary>
        /// <returns></returns>
        internal async Task RevokeCertificatesUnattended()
        {
            _log.Warning($"Certificates should only be revoked in case of a (suspected) security breach. Cancel the renewal if you simply don't need the certificate anymore.");
            var renewals = FilterRenewalsByCommandLine("revoke");
            await RevokeCertificates(renewals);
        }

        /// <summary>
        /// Shared code for command line and renewal manager
        /// </summary>
        /// <param name="renewals"></param>
        /// <returns></returns>
        internal async Task RevokeCertificates(IEnumerable<Renewal> renewals)
        {
            foreach (var renewal in renewals)
            {
                using var scope = _scopeBuilder.Execution(_container, renewal, RunLevel.Unattended);
                var cs = scope.Resolve<ICertificateService>();
                try
                {
                    await cs.RevokeCertificate(renewal);
                    renewal.History.Add(new RenewResult("Certificate(s) revoked"));
                }
                catch (Exception ex)
                {
                    _exceptionHandler.HandleException(ex);
                }
            }
        }

        #endregion

    }
}
