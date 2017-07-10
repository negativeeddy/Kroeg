﻿using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Kroeg.Server.Models;
using Kroeg.Server.Services.EntityStore;
using Kroeg.Server.Tools;
using Newtonsoft.Json;
using Kroeg.Server.Services;
using Kroeg.ActivityStreams;
using Kroeg.Server.Middleware;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Linq;

namespace Kroeg.Server.BackgroundTasks
{
    public class DeliverToActivityPubData
    {
        public string TargetInbox { get; set; }
        public string ObjectId { get; set; }
    }

    public class DeliverToActivityPubTask : BaseTask<DeliverToActivityPubData, DeliverToActivityPubTask>
    {
        private readonly IEntityStore _entityStore;
        private readonly EntityFlattener _entityFlattener;
        private readonly IServiceProvider _serviceProvider;
        private readonly DeliveryService _deliveryService;

        public DeliverToActivityPubTask(EventQueueItem item, IEntityStore entityStore, EntityFlattener entityFlattener, IServiceProvider serviceProvider, DeliveryService deliveryService) : base(item)
        {
            _entityStore = entityStore;
            _entityFlattener = entityFlattener;
            _serviceProvider = serviceProvider;
            _deliveryService = deliveryService;
        }

        public async Task PostToServer()
        {
            var entity = await _entityStore.GetEntity(Data.ObjectId, false);
            var owner = await _entityStore.GetEntity((string) entity.Data["actor"].First().Primitive, false);
            var unflattened = await _entityFlattener.Unflatten(_entityStore, entity);

            var token = await _deliveryService.BuildFederatedJWS(owner, Data.TargetInbox);

            var hc = new HttpClient();
            var serialized = unflattened.Serialize(true).ToString(Formatting.None);
            var content = new StringContent(serialized, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ld+json");
            content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("profile", "\"https://www.w3.org/ns/activitystreams\""));
            hc.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/ld+json; profile=\"https://www.w3.org/ns/activitystreams\"");
            hc.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var result = await hc.PostAsync(Data.TargetInbox, content);

            var resultContent = await result.Content.ReadAsStringAsync();
            if (!result.IsSuccessStatusCode && (int)result.StatusCode / 100 == 5)
                throw new Exception("Failed to deliver. Retrying later.");
        }

        public override async Task Go()
        {
            var inbox = await _entityStore.GetEntity(Data.TargetInbox, false);
            if (inbox != null && inbox.IsOwner && inbox.Type == "_inbox")
            {
                var item = await _entityStore.GetEntity(Data.ObjectId, false);

                var claims = new ClaimsPrincipal();
                var handler = ActivatorUtilities.CreateInstance<GetEntityMiddleware.GetEntityHandler>(_serviceProvider, claims);
                try
                {
                    await handler.ServerToServer(inbox, item.Data);
                }
                catch (UnauthorizedAccessException) { }
                catch (InvalidOperationException) { }
            }
            else
            {
                await PostToServer();
            }
        }
    }
}
