﻿using IdentityServer4.Models;
using IdentityServer4.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IdentityServer4.Contrib.RedisStore.Stores
{
    public class PersistedGrantStore : IPersistedGrantStore
    {
        private readonly IDatabase database;

        private readonly ILogger<PersistedGrantStore> logger;

        public PersistedGrantStore(IDatabase database, ILogger<PersistedGrantStore> logger)
        {
            this.database = database ?? throw new ArgumentNullException(nameof(database));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private static string GetKey(string key) => key;

        private static string GetSetKey(string subjectId) => subjectId;

        private static string GetSetKey(string subjectId, string clientId) => $"{subjectId}:{clientId}";

        private static string GetSetKey(string subjectId, string clientId, string type) => $"{subjectId}:{clientId}:{type}";

        public async Task StoreAsync(PersistedGrant grant)
        {
            if (grant == null)
                throw new ArgumentNullException(nameof(grant));
            try
            {
                var data = ConvertToJson(grant);
                var grantKey = GetKey(grant.Key);
                var expiresIn = grant.Expiration - DateTime.UtcNow;
                if (!string.IsNullOrEmpty(grant.SubjectId))
                {
                    var setKey = GetSetKey(grant.SubjectId, grant.ClientId, grant.Type);
                    var transaction = this.database.CreateTransaction();
                    transaction.StringSetAsync(grantKey, data, expiresIn);
                    transaction.SetAddAsync(GetSetKey(grant.SubjectId), grantKey);
                    transaction.SetAddAsync(GetSetKey(grant.SubjectId, grant.ClientId), grantKey);
                    transaction.SetAddAsync(setKey, grantKey);
                    transaction.KeyExpireAsync(setKey, expiresIn);
                    await transaction.ExecuteAsync();
                }
                else
                {
                    await this.database.StringSetAsync(grantKey, data, expiresIn);
                }
                logger.LogDebug($"grant for subject {grant.SubjectId}, clientId {grant.ClientId}, grantType {grant.Type} persisted successfully");
            }
            catch (Exception ex)
            {
                logger.LogWarning($"exception storing persisted grant to Redis database for subject {grant.SubjectId}, clientId {grant.ClientId}, grantType {grant.Type} : {ex.Message}");
            }

        }

        public async Task<PersistedGrant> GetAsync(string key)
        {
            var data = await this.database.StringGetAsync(GetKey(key));
            logger.LogDebug($"{key} found in database: {data.HasValue}");
            return data.HasValue ? ConvertFromJson(data) : null;
        }

        public async Task<IEnumerable<PersistedGrant>> GetAllAsync(string subjectId)
        {
            var setKey = GetSetKey(subjectId);
            var grantsKeys = await this.database.SetMembersAsync(setKey);
            if (grantsKeys.Count() == 0)
                return Enumerable.Empty<PersistedGrant>();
            var grants = await this.database.StringGetAsync(grantsKeys.Select(_ => (RedisKey)_.ToString()).ToArray());
            var keysToDelete = grantsKeys.Zip(grants, (key, value) => new KeyValuePair<RedisValue, RedisValue>(key, value))
                                         .Where(_ => !_.Value.HasValue).Select(_ => _.Key);
            if (keysToDelete.Count() != 0)
                await this.database.SetRemoveAsync(setKey, keysToDelete.ToArray());
            logger.LogDebug($"{grantsKeys.Count(_ => _.HasValue)} persisted grants found for {subjectId}");
            return grants.Where(_ => _.HasValue).Select(_ => ConvertFromJson(_));
        }

        public async Task RemoveAsync(string key)
        {
            try
            {
                var grant = await this.GetAsync(key);
                if (grant == null)
                {
                    logger.LogDebug($"no {key} persisted grant found in database");
                    return;
                }
                var grantKey = GetKey(key);
                logger.LogDebug($"removing {key} persisted grant from database");
                var transaction = this.database.CreateTransaction();
                transaction.KeyDeleteAsync(grantKey);
                transaction.SetRemoveAsync(GetSetKey(grant.SubjectId), grantKey);
                transaction.SetRemoveAsync(GetSetKey(grant.SubjectId, grant.ClientId), grantKey);
                transaction.SetRemoveAsync(GetSetKey(grant.SubjectId, grant.ClientId, grant.Type), grantKey);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogInformation($"exception removing {key} persisted grant from database: {ex.Message}");
            }

        }

        public async Task RemoveAllAsync(string subjectId, string clientId)
        {
            try
            {
                var setKey = GetSetKey(subjectId, clientId);
                var grantsKeys = await this.database.SetMembersAsync(setKey);
                logger.LogDebug($"removing {grantsKeys.Count()} persisted grants from database for subject {subjectId}, clientId {clientId}");
                if (grantsKeys.Count() == 0) return;
                var transaction = this.database.CreateTransaction();
                transaction.KeyDeleteAsync(grantsKeys.Select(_ => (RedisKey)_.ToString()).Concat(new RedisKey[] { setKey }).ToArray());
                transaction.SetRemoveAsync(GetSetKey(subjectId), grantsKeys);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogInformation($"removing persisted grants from database for subject {subjectId}, clientId {clientId}: {ex.Message}");
            }
        }

        public async Task RemoveAllAsync(string subjectId, string clientId, string type)
        {
            try
            {
                var setKey = GetSetKey(subjectId, clientId, type);
                var grantsKeys = await this.database.SetMembersAsync(setKey);
                logger.LogDebug($"removing {grantsKeys.Count()} persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type}");
                if (grantsKeys.Count() == 0) return;
                var transaction = this.database.CreateTransaction();
                transaction.KeyDeleteAsync(grantsKeys.Select(_ => (RedisKey)_.ToString()).Concat(new RedisKey[] { setKey }).ToArray());
                transaction.SetRemoveAsync(GetSetKey(subjectId, clientId), grantsKeys);
                transaction.SetRemoveAsync(GetSetKey(subjectId), grantsKeys);
                await transaction.ExecuteAsync();
            }
            catch (Exception ex)
            {
                logger.LogInformation($"exception removing persisted grants from database for subject {subjectId}, clientId {clientId}, grantType {type}: {ex.Message}");
            }
        }

        #region Json
        private static string ConvertToJson(PersistedGrant grant)
        {
            return JsonConvert.SerializeObject(grant);
        }

        private static PersistedGrant ConvertFromJson(string data)
        {
            return JsonConvert.DeserializeObject<PersistedGrant>(data);
        }
        #endregion
    }
}
