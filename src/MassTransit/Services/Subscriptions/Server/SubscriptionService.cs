// Copyright 2007-2011 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Services.Subscriptions.Server
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Exceptions;
	using log4net;
	using Magnum.Extensions;
	using Messages;
	using Saga;
	using Stact;
	using Subscriptions.Messages;

	public class SubscriptionService :
		Consumes<SubscriptionClientAdded>.All,
		Consumes<SubscriptionClientRemoved>.All,
		Consumes<SubscriptionAdded>.All,
		Consumes<SubscriptionRemoved>.All,
		IDisposable
	{
		static readonly ILog _log = LogManager.GetLogger(typeof (SubscriptionService));
		readonly Fiber _fiber = new PoolFiber();
		readonly ISagaRepository<SubscriptionClientSaga> _subscriptionClientSagas;
		readonly ISagaRepository<SubscriptionSaga> _subscriptionSagas;
		IServiceBus _bus;
		bool _disposed;
		UnsubscribeAction _unsubscribeToken = () => false;

		public SubscriptionService(IServiceBus bus,
		                           ISagaRepository<SubscriptionSaga> subscriptionSagas,
		                           ISagaRepository<SubscriptionClientSaga> subscriptionClientSagas)
		{
			_bus = bus;
			_subscriptionSagas = subscriptionSagas;
			_subscriptionClientSagas = subscriptionClientSagas;
		}

		public void Consume(SubscriptionAdded message)
		{
			if (_log.IsInfoEnabled)
				_log.InfoFormat("Subscription Added: {0} [{1}]", message.Subscription, message.Subscription.CorrelationId);

			var add = new AddSubscription(message.Subscription);

			_fiber.Add(() => SendToClients(add));
		}

		public void Consume(SubscriptionClientAdded message)
		{
			if (_log.IsInfoEnabled)
				_log.InfoFormat("Subscription Client Added: {0} [{1}]", message.ControlUri, message.ClientId);

			_fiber.Add(() => SendCacheUpdateToClient(message.ControlUri));
		}

		public void Consume(SubscriptionClientRemoved message)
		{
			if (_log.IsInfoEnabled)
				_log.InfoFormat("Subscription Client Removed: {0} [{1}]", message.ControlUri, message.CorrelationId);
		}

		public void Consume(SubscriptionRemoved message)
		{
			if (_log.IsInfoEnabled)
				_log.InfoFormat("Subscription Removed: {0} [{1}]", message.Subscription, message.Subscription.CorrelationId);

			var remove = new RemoveSubscription(message.Subscription);

			_fiber.Add(() => SendToClients(remove));
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		public void Start()
		{
			_log.InfoFormat("Subscription Service Starting: {0}", _bus.Endpoint.Address);
			_unsubscribeToken += _bus.SubscribeInstance(this);

			_unsubscribeToken += _bus.SubscribeSaga<SubscriptionClientSaga>(_subscriptionClientSagas);
			_unsubscribeToken += _bus.SubscribeSaga<SubscriptionSaga>(_subscriptionSagas);

			// TODO may need to load/prime the subscription repository at this point?

			_log.Info("Subscription Service Started");
		}

		public void Stop()
		{
			_log.Info("Subscription Service Stopping");

			_unsubscribeToken();

			_log.Info("Subscription Service Stopped");
		}

		void Dispose(bool disposing)
		{
			if (_disposed) return;
			if (disposing)
			{
				try
				{
					_fiber.Shutdown(60.Seconds());

					_bus.Dispose();
					_bus = null;
				}
				catch (Exception ex)
				{
					string message = "Error in shutting down the SubscriptionService: " + ex.Message;
					var exp = new ShutDownException(message, ex);
					_log.Error(message, exp);
					throw exp;
				}
			}

			_disposed = true;
		}

		void SendToClients<T>(T message)
			where T : SubscriptionChange
		{
			IEnumerable<SubscriptionClientSaga> sagas = _subscriptionClientSagas.Where(x => x.CurrentState == SubscriptionClientSaga.Active);

			_log.DebugFormat("Sending {0}:{1} to {2} clients", typeof (T).Name, message.Subscription.MessageName, sagas.Count());
			sagas.Each(client =>
					{
						IEndpoint endpoint = _bus.GetEndpoint(client.ControlUri);

						endpoint.Send(message, x => x.SetSourceAddress(_bus.Endpoint.Uri));
					});
		}

		void SendCacheUpdateToClient(Uri uri)
		{
			IEnumerable<SubscriptionInformation> subscriptions = _subscriptionSagas.Where(
				x => x.CurrentState == SubscriptionSaga.Active)
				.Select(x => x.SubscriptionInfo);

			var response = new SubscriptionRefresh(subscriptions);

			IEndpoint endpoint = _bus.GetEndpoint(uri);

			endpoint.Send(response, x => x.SetSourceAddress(_bus.Endpoint.Uri));
		}

		~SubscriptionService()
		{
			Dispose(false);
		}
	}
}