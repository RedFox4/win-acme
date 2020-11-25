﻿using System;
using System.Linq;
using System.Threading.Tasks;
using DigitalOcean.API;
using DigitalOcean.API.Models.Requests;
using PKISharp.WACS.Clients.DNS;
using PKISharp.WACS.Services;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Dns
{
    internal class DigitalOcean : DnsValidation<DigitalOcean>
    {
        private readonly IDigitalOceanClient _doClient;
        private long? _recordId;
        private string _zone;

        public DigitalOcean(DigitalOceanOptions options, LookupClientProvider dnsClient, ILogService log, ISettingsService settings) : base(dnsClient, log, settings) 
            => _doClient = new DigitalOceanClient(options.ApiToken.Value);

        public override async Task DeleteRecord(DnsValidationRecord record)
        {
            try
            {
                if (_recordId == null)
                {
                    _log.Warning("Not deleting DNS records on DigitalOcean because of missing record id.");
                    return;
                }

                await _doClient.DomainRecords.Delete(_zone, _recordId.Value);
                _log.Information("Successfully deleted DNS record on DigitalOcean.");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to delete DNS record on DigitalOcean.");
            }
        }

        public override async Task<bool> CreateRecord(DnsValidationRecord record)
        {
            try
            {
                var zones = await _doClient.Domains.GetAll();
                var zone = FindBestMatch(zones.Select(x => x.Name).ToDictionary(x => x), record.Authority.Domain);
                if (zone == null)
                {
                    _log.Error($"Unable to find a zone on DigitalOcean for '{record.Authority.Domain}'.");
                    return false;
                }

                var createdRecord = await _doClient.DomainRecords.Create(zone, new DomainRecord
                {
                    Type = "TXT",
                    // If a zone exists that has the exact name of the record to be created, the record needs to be named "@"
                    Name = record.Authority.Domain == zone
                        ? "@"
                        : record.Authority.Domain[..^(zone.Length + 1)],
                    Data = record.Value,
                    Ttl = 300
                });
                _recordId = createdRecord.Id;
                _zone = zone;
                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create DNS record on DigitalOcean.");
                return false;
            }
        }
    }
}
