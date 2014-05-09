﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace WebApiThrottle
{
    /// <summary>
    /// Throttle message handler
    /// </summary>
    public class ThrottlingHandler : DelegatingHandler
    {
        private ThrottlingCore core;

        /// <summary>
        /// Creates a new instance of the <see cref="ThrottlingHandler"/> class.
        /// By default, the <see cref="QuotaExceededResponseCode"/> property 
        /// is set to 429 (Too Many Requests).
        /// </summary>
        public ThrottlingHandler()
        {
            QuotaExceededResponseCode = (HttpStatusCode)429;
            Repository = new CacheRepository();
            core = new ThrottlingCore();
        }

        private IPolicyRepository policyRepository;

        /// <summary>
        ///  Throttling rate limits policy repository
        /// </summary>
        public IPolicyRepository PolicyRepository
        {
            get { return policyRepository; }
            set { policyRepository = value; }
        }

        private ThrottlePolicy policy;

        /// <summary>
        /// Throttling rate limits policy
        /// </summary>
        public ThrottlePolicy Policy
        {
            get { return policy; }
            set { policy = value; }
        }

        /// <summary>
        /// Throttle metrics storage
        /// </summary>
        public IThrottleRepository Repository { get; set; }

        /// <summary>
        /// Log traffic and blocked requests
        /// </summary>
        public IThrottleLogger Logger { get; set; }

        /// <summary>
        /// If none specifed the default will be: 
        /// API calls quota exceeded! maximum admitted {0} per {1}
        /// </summary>
        public string QuotaExceededMessage { get; set; }

        /// <summary>
        /// Gets or sets the value to return as the HTTP status 
        /// code when a request is rejected because of the
        /// throttling policy. The default value is 429 (Too Many Requests).
        /// </summary>
        public HttpStatusCode QuotaExceededResponseCode { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            //get policy from repo
            if (policyRepository != null)
            {
                policy = policyRepository.FirstOrDefault(ThrottleManager.GetPolicyKey());
            }

            if (policy == null || (!policy.IpThrottling && !policy.ClientThrottling && !policy.EndpointThrottling))
            {
                return base.SendAsync(request, cancellationToken);
            }

            core.Repository = Repository;
            core.Policy = policy;

            var identity = SetIndentity(request);

            if (core.IsWhitelisted(identity))
            {
                return base.SendAsync(request, cancellationToken);
            }

            TimeSpan timeSpan = TimeSpan.FromSeconds(1);

            //get default rates
            var defRates = core.RatesWithDefaults(Policy.Rates.ToList());
            if (Policy.StackBlockedRequests)
            {
                //all requests including the rejected ones will stack in this order: week, day, hour, min, sec
                //if a client hits the hour limit then the minutes and seconds counters will expire and will eventually get erased from cache
                defRates.Reverse();
            }

            //apply policy
            foreach (var rate in defRates)
            {
                var rateLimitPeriod = rate.Key;
                var rateLimit = rate.Value;

                timeSpan = core.GetTimeSpanFromPeriod(rateLimitPeriod);

                //apply global rules
                core.ApplyRules(identity, timeSpan, rateLimitPeriod, ref rateLimit);

                if (rateLimit > 0)
                {
                    //increment counter
                    string requestId;
                    var throttleCounter = core.ProcessRequest(identity, timeSpan, rateLimitPeriod, out requestId);

                    //check if key expired
                    if (throttleCounter.Timestamp + timeSpan < DateTime.UtcNow)
                        continue;

                    //check if limit is reached
                    if (throttleCounter.TotalRequests > rateLimit)
                    {
                        //log blocked request
                        if (Logger != null) Logger.Log(core.ComputeLogEntry(requestId, identity, throttleCounter, rateLimitPeriod.ToString(), rateLimit, request));

                        string message;
                        if (!string.IsNullOrEmpty(QuotaExceededMessage))
                            message = QuotaExceededMessage;
                        else
                            message = "API calls quota exceeded! maximum admitted {0} per {1}.";

                        //break execution
                        return QuotaExceededResponse(request,
                            string.Format(message, rateLimit, rateLimitPeriod),
                            QuotaExceededResponseCode,
                            core.RetryAfterFrom(throttleCounter.Timestamp, rateLimitPeriod));
                    }
                }
            }

            //no throttling required
            return base.SendAsync(request, cancellationToken);
        }

        protected IPAddress GetClientIp(HttpRequestMessage request)
        {
            return core.GetClientIp(request);
        }

        protected virtual RequestIdentity SetIndentity(HttpRequestMessage request)
        {
            var entry = new RequestIdentity();
            entry.ClientIp = core.GetClientIp(request).ToString();
            entry.Endpoint = request.RequestUri.AbsolutePath.ToLowerInvariant();
            entry.ClientKey = request.Headers.Contains("Authorization-Token") ? request.Headers.GetValues("Authorization-Token").First() : "anon";

            return entry;
        }

        protected virtual string ComputeThrottleKey(RequestIdentity requestIdentity, RateLimitPeriod period)
        {
            return core.ComputeThrottleKey(requestIdentity, period);
        }

        protected virtual Task<HttpResponseMessage> QuotaExceededResponse(HttpRequestMessage request, string message, HttpStatusCode responseCode, string retryAfter)
        {
            var response = request.CreateResponse(responseCode, message);
            response.Headers.Add("Retry-After", new string[] { retryAfter });
            return Task.FromResult(response);
        }
    }
}
