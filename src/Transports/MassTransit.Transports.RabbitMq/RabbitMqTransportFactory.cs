﻿// Copyright 2007-2011 The Apache Software Foundation.
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
namespace MassTransit.Transports.RabbitMq
{
	using System;
	using Exceptions;
	using Magnum.Collections;
	using RabbitMQ.Client;
	using System.Linq;
	using Magnum.Extensions;

    public class RabbitMqTransportFactory :
        ITransportFactory, IDisposable
    {
        static Cache<ServerKey, IConnection> _connectionFactoryCache = new Cache<ServerKey, IConnection>(a =>
        {
            return new ConnectionFactory
                {
                    HostName = a.Host,
                    VirtualHost = a.VHost,
                    Port = a.Port,
                    UserName = a.Username,
                    Password = a.Password,
                }.CreateConnection();
        });

        public string Scheme
        {
            get { return "rabbitmq"; }
        }

		public IDuplexTransport BuildLoopback(ITransportSettings settings)
        {
            //need to setup addresses
            // rabbitmq://server/loopback?/<name>

            //build duplex address
            var transport = new LoopbackRabbitMqTransport(settings.Address, BuildInbound(settings), BuildOutbound(settings));
            return transport;
        }

        public IInboundTransport BuildInbound(ITransportSettings settings)
        {
            EnsureProtocolIsCorrect(settings.Address.Uri);

            var address = settings.Address.CastAs<RabbitMqAddress>();
            
            //rabbitmq://server:port/queue/<queue>
            var connection = _connectionFactoryCache[ToKey(address)];

            return new InboundRabbitMqTransport(address, connection);
        }

        public IOutboundTransport BuildOutbound(ITransportSettings settings)
        {
            EnsureProtocolIsCorrect(settings.Address.Uri);

            var address = settings.Address.CastAs<RabbitMqAddress>();

            //rabbitmq://server:port/queue/<queue> -> rabbitmq://server:port/exchange/queue/<queue>
            //rabbitmq://server:port/exchange/<message:urn>
            var connection = _connectionFactoryCache[ToKey(address)];

            return new OutboundRabbitMqTransport(address, connection);
        }

        public IOutboundTransport BuildError(ITransportSettings settings)
        {
            return BuildOutbound(settings);
        }


        private static void EnsureProtocolIsCorrect(Uri address)
        {
            if (address.Scheme != "rabbitmq")
                throw new EndpointException(address, "Address must start with 'rabbitmq' not '{0}'".FormatWith(address.Scheme));
        }

        public void Dispose()
        {
            _connectionFactoryCache.Each(conn => conn.Close());
            _connectionFactoryCache.ClearAll();
            _connectionFactoryCache = null;
        }

        public int ConnectionsCount()
        {
            return _connectionFactoryCache.Count();
        }

        class ServerKey
        {
            public readonly string Host;
            public readonly string VHost;
            public readonly int Port;
            public readonly string Username;
            public readonly string Password;

            public ServerKey(RabbitMqAddress address)
            {
                Host = address.Host;
                VHost = address.VHost;
                Port = address.Port;
                Username = address.Username;
                Password = address.Password;
            }

            #region equality

            public bool Equals(ServerKey other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(other.Host, Host) && Equals(other.VHost, VHost) && other.Port == Port && Equals(other.Username, Username) && Equals(other.Password, Password);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != typeof (ServerKey)) return false;
                return Equals((ServerKey) obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int result = (Host != null ? Host.GetHashCode() : 0);
                    result = (result*397) ^ (VHost != null ? VHost.GetHashCode() : 0);
                    result = (result*397) ^ Port;
                    result = (result*397) ^ (Username != null ? Username.GetHashCode() : 0);
                    result = (result*397) ^ (Password != null ? Password.GetHashCode() : 0);
                    return result;
                }
            }

            #endregion
        }

        static ServerKey ToKey(RabbitMqAddress addr)
        {
            return new ServerKey(addr);
        }

        public void Bind(Uri queue, Uri exchange, string exchangeType)
        {
            var q = RabbitMqAddress.Parse(queue);
            var e = RabbitMqAddress.Parse(exchange);

            using(var connection = _connectionFactoryCache[ToKey(q)])
            using(var model = connection.CreateModel())
            {
                model.QueueDeclare(q.Path, true, true, true, null);
                model.ExchangeDeclare(e.Path, exchangeType, true);

                model.QueueBind(q.Path, e.Path, "", null);
            }
        }
    }


}