using CentCom.Common.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CentCom.Common.Data;
using CentCom.Server.Exceptions;
using CentCom.Server.Extensions;
using CentCom.Common.Models.Equality;

namespace CentCom.Server.BanSources
{
    [DisallowConcurrentExecution]
    public abstract class BanParser : IJob
    {
        protected ILogger _logger;
        protected DatabaseContext _dbContext { get; set; }
        /// <summary>
        /// A map of BanSource.Name, BanSource containing the 'offline' skeletons of the ban sources
        /// for this ban parser. Necessary for creating the sources initially in the database.
        /// </summary>
        public virtual Dictionary<string, BanSource> Sources { get; }
        /// <summary>
        /// Boolean operator detailing if the ban source exposes their own ban IDs in their API
        /// </summary>
        public virtual bool SourceSupportsBanIDs { get; }

        public BanParser(DatabaseContext dbContext, ILogger<BanParser> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        /// <summary>
        /// Executes the ban parsing job
        /// </summary>
        /// <remarks>
        /// This also handles the proper handling of unexpected exceptions to prevent infinite job looping, jobs
        /// will instead execute again at the next scheduled trigger.
        /// </remarks>
        /// <param name="context">The job execution context provided by Quartz' scheduler</param>
        /// <returns>A task for the asynchronous work</returns>
        public virtual async Task Execute(IJobExecutionContext context)
        {
            try
            {
                await ParseBans(context);
            }
            catch (JobExecutionException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Encountered unhandled exception during ban parsing");
                throw new JobExecutionException(ex, false);
            }
        }

        /// <summary>
        /// Attempts to fetch and process bans from the source for the ban parser.
        /// </summary>
        /// <param name="context">The job execution context provided by Quartz' scheduler</param>
        /// <returns>A task for the asynchronous work</returns>
        public virtual async Task ParseBans(IJobExecutionContext context)
        {
            _logger.LogInformation($"Beginning ban parsing");
            
            // Get stored bans from the database
            IEnumerable<Ban> storedBans = null;
            try
            {
                storedBans = await _dbContext.Bans
                .Where(x => Sources.Keys.Contains(x.SourceNavigation.Name))
                .Include(x => x.JobBans)
                .Include(x => x.SourceNavigation)
                .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get stored ban data from database, encountered exception");
                throw new JobExecutionException(ex, false);
            }

            // Get bans from the source
            var isCompleteRefresh = context.MergedJobDataMap.GetBoolean("completeRefresh") || storedBans.Count() == 0;
            IEnumerable<Ban> bans = null;
            try
            {
                bans = await (isCompleteRefresh ? FetchAllBansAsync() : FetchNewBansAsync());
            }
            catch (BanSourceUnavailableException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get ban data from source, encountered exception during fetch");
                throw new JobExecutionException(ex, false);
            }

            // Assign proper sources
            bans = await AssignBanSources(bans);

            // Check for ban updates
            var inserted = 0;
            var updated = 0;
            foreach (var b in bans)
            {
                // Enssure the CKey is actually canonical
                b.MakeKeysCanonical();

                // Attempt to find matching bans in the database
                Ban matchedBan = null;
                if (SourceSupportsBanIDs)
                {
                    matchedBan = storedBans.FirstOrDefault(x => 
                        b.Source == x.Source
                        && b.BanID == x.BanID);
                }
                else
                {
                    matchedBan = storedBans.FirstOrDefault(x =>
                        b.Source == x.Source
                        && b.BannedOn == x.BannedOn
                        && b.BanType == x.BanType
                        && b.CKey == x.CKey
                        && b.BannedBy == x.BannedBy
                        && (b.BanType == BanType.Server 
                            || b.JobBans.SetEquals(x.JobBans)));
                }

                // Update ban if an existing one is found
                if (matchedBan != null)
                {
                    // Check for a difference in date time, unbans, or reason
                    if (matchedBan.Reason != b.Reason || matchedBan.Expires != b.Expires || matchedBan.UnbannedBy != b.UnbannedBy)
                    {
                        matchedBan.Reason = b.Reason;
                        matchedBan.Expires = b.Expires;
                        matchedBan.UnbannedBy = b.UnbannedBy;
                        updated++;
                    }

                    // Check for a difference in recorded jobbans
                    if (b.BanType == BanType.Job && !b.JobBans.SetEquals(matchedBan.JobBans))
                    {
                        matchedBan.JobBans = new HashSet<JobBan>(JobBanEqualityComparer.Instance);
                        matchedBan.AddJobRange(b.JobBans.Select(x => x.Job));
                        updated++;
                    }
                }
                // Otherwise add insert a new ban
                else
                {
                    inserted++;
                    _dbContext.Bans.Add(b);
                }
            }

            // Insert new changes
            _logger.LogInformation($"Inserting {inserted} new bans, updating {updated} modified bans...");
            await _dbContext.SaveChangesAsync();

            // Delete any missing bans if we're doing a complete refresh
            if (isCompleteRefresh)
            {
                var bansHashed = new HashSet<Ban>(bans, BanEqualityComparer.Instance);
                var missingBans = storedBans.Except(bansHashed, BanEqualityComparer.Instance).ToList();

                if (bansHashed.Count == 0 && missingBans.Count > 1)
                {
                    throw new Exception("Failed to find any bans for source, aborting removal phase of ban " +
                        "parsing to avoid dumping entire set of bans");
                }

                // Apply deletions
                _logger.LogInformation(missingBans.Count > 0 ? $"Removing {missingBans.Count} deleted bans..." 
                    : "Found no deleted bans to remove");
                if (missingBans.Count > 0)
                {
                    _dbContext.RemoveRange(missingBans);
                    await _dbContext.SaveChangesAsync();
                }
            }

            _logger.LogInformation("Completed ban parsing.");
        }

        /// <summary>
        /// Gets all BanSource objects from the connected database
        /// </summary>
        /// <returns>A dictionary of the BanSource objects found from the database</returns>
        public async Task<Dictionary<string, BanSource>> GetSourcesAsync()
        {
            if (Sources == null)
            {
                throw new NullReferenceException($"Sources for {GetType()} are null.");
            }

            // Get ban sources from the database
            var foundSources = await _dbContext.BanSources.Where(x => Sources.Keys.Contains(x.Name)).ToListAsync();

            // Insert any ban sources that are missing, this is vital to ensure the database is properly configured state-wise
            if (foundSources.Count() != Sources.Count)
            {
                var missing = Sources.Keys.Except(foundSources.Select(x => x.Name)).ToList();
                foreach (var source in missing)
                {
                    _dbContext.BanSources.Add(Sources[source]);
                }
                await _dbContext.SaveChangesAsync();
                foundSources = await _dbContext.BanSources.Where(x => Sources.Keys.Contains(x.Name)).ToListAsync();
            }

            return foundSources.ToDictionary(x => x.Name);
        }

        /// <summary>
        /// Maps the correct BanSource database object to the placeholder objects on provided Ban objects.
        /// </summary>
        /// <remarks>
        /// Used for setting the correct BanSource prior to database insertion or interaction
        /// </remarks>
        /// <param name="bans">A collection of bans to have their source objects assigned</param>
        /// <returns>A collection of bans which have correct database-backed BanSource objects assigned</returns>
        public async Task<IEnumerable<Ban>> AssignBanSources(IEnumerable<Ban> bans)
        {
            var sources = await GetSourcesAsync();
            foreach (var b in bans)
            {
                b.SourceNavigation = sources[b.SourceNavigation.Name];
                b.Source = b.SourceNavigation.Id;
            }
            return bans;
        }

        /// <summary>
        /// Attempts to fetch new unseen bans from the ban source
        /// </summary>
        /// <remarks>
        /// This can include existing bans, the BanParser will handle them correctly, the intention is 
        /// just to limit the response size
        /// </remarks>
        /// <returns>A collection of bans found from the source</returns>
        public abstract Task<IEnumerable<Ban>> FetchNewBansAsync();

        /// <summary>
        /// Attempts to fetch all bans from the ban source
        /// </summary>
        /// <returns>A collection of bans found from the source</returns>
        public abstract Task<IEnumerable<Ban>> FetchAllBansAsync();
    }
}
