﻿using Org.Apache.Zookeeper.Data;
using Rabbit.Rpc.Address;
using Rabbit.Rpc.Routing;
using Rabbit.Rpc.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZooKeeperNet;

namespace Rabbit.Rpc.Coordinate.Zookeeper
{
    /// <summary>
    /// 基于zookeeper的服务路由管理者。
    /// </summary>
    public class ZooKeeperServiceRouteManager : IServiceRouteManager, IDisposable
    {
        #region Field

        private ZooKeeper _zooKeeper;
        private readonly ZookeeperConfigInfo _configInfo;
        private readonly ISerializer _serializer;
        private IEnumerable<ServiceRoute> _routes;
        private readonly ManualResetEvent _connectionWait = new ManualResetEvent(false);

        #endregion Field

        #region Constructor

        public ZooKeeperServiceRouteManager(ZookeeperConfigInfo configInfo, ISerializer serializer)
        {
            _configInfo = configInfo;
            _serializer = serializer;
            CreateZooKeeper();
            CreateSubdirectory(configInfo.RoutePath);
            EnterRoutes();
        }

        #endregion Constructor

        #region Implementation of IServiceRouteManager

        /// <summary>
        /// 获取所有可用的服务路由信息。
        /// </summary>
        /// <returns>服务路由集合。</returns>
        public Task<IEnumerable<ServiceRoute>> GetRoutesAsync()
        {
            EnterRoutes();
            return Task.FromResult(_routes);
        }

        /// <summary>
        /// 添加服务路由。
        /// </summary>
        /// <param name="routes">服务路由集合。</param>
        /// <returns>一个任务。</returns>
        public Task AddRoutesAsync(IEnumerable<ServiceRoute> routes)
        {
            return Task.Run(() =>
            {
                var path = _configInfo.RoutePath;
                if (!path.EndsWith("/"))
                    path += "/";
                foreach (var serviceRoute in routes)
                {
                    var nodePath = $"{path}{serviceRoute.ServiceDescriptor.Id}";
                    var nodeData = _serializer.Serialize(serviceRoute);
                    if (_zooKeeper.Exists(nodePath, false) == null)
                    {
                        _zooKeeper.Create(nodePath, nodeData, ZooKeeperNet.Ids.OPEN_ACL_UNSAFE, CreateMode.Persistent);
                    }
                    else
                    {
                        _zooKeeper.SetData(nodePath, nodeData, -1);
                    }
                }
            });
        }

        /// <summary>
        /// 清空所有的服务路由。
        /// </summary>
        /// <returns>一个任务。</returns>
        public Task ClearAsync()
        {
            return Task.Run(() =>
            {
                var path = _configInfo.RoutePath;
                var childrens = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                var index = 0;
                while (childrens.Any())
                {
                    var nodePath = "/" + string.Join("/", childrens);

                    if (_zooKeeper.Exists(nodePath, false) != null)
                    {
                        foreach (var child in _zooKeeper.GetChildren(nodePath, false))
                        {
                            _zooKeeper.Delete($"{nodePath}/{child}", -1);
                        }
                        _zooKeeper.Delete(nodePath, -1);
                    }
                    index++;
                    childrens = childrens.Take(childrens.Length - index).ToArray();
                }
            });
        }

        #endregion Implementation of IServiceRouteManager

        #region Private Method

        private void CreateZooKeeper()
        {
            _zooKeeper?.Dispose();
            _zooKeeper = new ZooKeeper(_configInfo.ConnectionString, _configInfo.SessionTimeout
                , new ReconnectionWatcher(
                () =>
                {
                    _connectionWait.Set();
                },
                () =>
                {
                    _connectionWait.Reset();
                    CreateZooKeeper();
                }));
        }

        private void CreateSubdirectory(string path)
        {
            _connectionWait.WaitOne();
            if (_zooKeeper.Exists(path, false) != null)
                return;
            var childrens = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var nodePath = "/";

            foreach (var children in childrens)
            {
                nodePath += children;
                if (_zooKeeper.Exists(nodePath, false) == null)
                {
                    _zooKeeper.Create(nodePath, null, ZooKeeperNet.Ids.OPEN_ACL_UNSAFE, CreateMode.Persistent);
                }
                nodePath += "/";
            }
        }

        private ServiceRoute GetRoute(byte[] data)
        {
            if (data == null)
                return null;

            var descriptor = _serializer.Deserialize<IpAddressDescriptor>(data);

            return new ServiceRoute
            {
                Address = descriptor.Address,
                ServiceDescriptor = descriptor.ServiceDescriptor
            };
        }

        private IEnumerable<ServiceRoute> GetRoutes(IEnumerable<string> childrens)
        {
            var rootPath = _configInfo.RoutePath;
            if (!rootPath.EndsWith("/"))
                rootPath += "/";
            foreach (var children in childrens)
            {
                var nodePath = $"{rootPath}{children}";
                var watcher = new NodeMonitorWatcher(_zooKeeper, nodePath, newData =>
                  {
                      var route = GetRoute(newData);
                      //删除旧路由。
                      _routes = _routes.Where(i => i.ServiceDescriptor.Id != route.ServiceDescriptor.Id);
                      //添加新路由。
                      if (route != null)
                          _routes = _routes.Concat(new[] { route });
                      _routes = _routes.ToArray();
                  });
                var data = _zooKeeper.GetData(nodePath, watcher, new Stat());
                yield return GetRoute(data);
            }
        }

        private void EnterRoutes()
        {
            if (_routes != null)
                return;
            _connectionWait.WaitOne();

            var watcher = new ChildrenMonitorWatcher(_zooKeeper, _configInfo.RoutePath, newChildrens =>
            {
                if (newChildrens == null)
                {
                    _routes = Enumerable.Empty<ServiceRoute>();
                    return;
                }
                //最新的节点数据。
                newChildrens = newChildrens.ToArray();

                //旧的节点数据。
                var outChildrens = _routes.Select(i => i.ServiceDescriptor.Id).ToArray();
                //计算出已被删除的节点。
                var deletedChildrens = outChildrens.Except(newChildrens);
                //结算出新增的节点。
                var createdChildrens = newChildrens.Except(outChildrens);

                //删除无效的节点路由。
                _routes = _routes.Where(i => !deletedChildrens.Contains(i.ServiceDescriptor.Id));
                //获取新增的路由信息。
                var newRoutes = GetRoutes(createdChildrens);
                _routes = _routes.Concat(newRoutes);

                _routes = _routes.ToArray();
            });
            if (_zooKeeper.Exists(_configInfo.RoutePath, watcher) != null)
            {
                var childrens = _zooKeeper.GetChildren(_configInfo.RoutePath, watcher, new Stat());
                _routes = GetRoutes(childrens);
            }
            else
                _routes = Enumerable.Empty<ServiceRoute>();
        }

        #endregion Private Method

        #region Watcher Class

        protected class ReconnectionWatcher : IWatcher
        {
            private readonly Action _connectioned;
            private readonly Action _disconnect;

            public ReconnectionWatcher(Action connectioned, Action disconnect)
            {
                _connectioned = connectioned;
                _disconnect = disconnect;
            }

            #region Implementation of IWatcher

            public void Process(WatchedEvent @event)
            {
                if (@event.State == KeeperState.SyncConnected)
                {
                    _connectioned();
                }
                else
                {
                    _disconnect();
                }
            }

            #endregion Implementation of IWatcher
        }

        protected abstract class WatcherBase : IWatcher
        {
            protected string Path { get; }

            protected WatcherBase(string path)
            {
                Path = path;
            }

            #region Implementation of IWatcher

            public void Process(WatchedEvent watchedEvent)
            {
                if (watchedEvent.State != KeeperState.SyncConnected || watchedEvent.Path != Path)
                    return;
                ProcessImpl(watchedEvent);
            }

            #endregion Implementation of IWatcher

            protected abstract void ProcessImpl(WatchedEvent watchedEvent);
        }

        protected class NodeMonitorWatcher : WatcherBase
        {
            private readonly ZooKeeper _zooKeeper;
            private readonly Action<byte[]> _action;

            public NodeMonitorWatcher(ZooKeeper zooKeeper, string path, Action<byte[]> action) : base(path)
            {
                _zooKeeper = zooKeeper;
                _action = action;
            }

            #region Overrides of WatcherBase

            protected override void ProcessImpl(WatchedEvent watchedEvent)
            {
                var path = Path;
                switch (watchedEvent.Type)
                {
                    case EventType.NodeDataChanged:
                        var data = _zooKeeper.GetData(path, new NodeMonitorWatcher(_zooKeeper, path, _action), new Stat());
                        _action(data);
                        break;

                    case EventType.NodeDeleted:
                        _action(null);
                        break;
                }
            }

            #endregion Overrides of WatcherBase
        }

        protected class ChildrenMonitorWatcher : WatcherBase
        {
            private readonly ZooKeeper _zooKeeper;
            private readonly Action<IEnumerable<string>> _action;

            public ChildrenMonitorWatcher(ZooKeeper zooKeeper, string path, Action<IEnumerable<string>> action) : base(path)
            {
                _zooKeeper = zooKeeper;
                _action = action;
            }

            #region Overrides of WatcherBase

            protected override void ProcessImpl(WatchedEvent watchedEvent)
            {
                var path = Path;
                var watcher = new ChildrenMonitorWatcher(_zooKeeper, path, _action);
                switch (watchedEvent.Type)
                {
                    case EventType.NodeCreated:
                    case EventType.NodeChildrenChanged:
                        if (_zooKeeper.Exists(path, watcher) != null)
                        {
                            var childrens = _zooKeeper.GetChildren(path, watcher, new Stat());
                            _action(childrens);
                        }
                        else
                        {
                            _action(null);
                        }
                        break;

                    case EventType.NodeDeleted:
                        _zooKeeper.Exists(path, watcher);
                        _action(null);
                        break;
                }
            }

            #endregion Overrides of WatcherBase
        }

        #endregion Watcher Class

        #region Help Class

        /// <summary>
        /// zookeeper连接信息。
        /// </summary>
        public class ZookeeperConfigInfo
        {
            /// <summary>
            /// 初始化一个会话超时为20秒的Zookeeper连接信息。
            /// </summary>
            /// <param name="connectionString">连接字符串。</param>
            /// <param name="routePath">路由配置路径。</param>
            /// <param name="chRoot">根节点。</param>
            public ZookeeperConfigInfo(string connectionString, string routePath = "/dotnet/serviceRoutes", string chRoot = null) : this(connectionString, TimeSpan.FromSeconds(20), routePath, chRoot)
            {
            }

            /// <summary>
            /// 初始化一个新的Zookeeper连接信息。
            /// </summary>
            /// <param name="connectionString">连接字符串。</param>
            /// <param name="routePath">路由配置路径。</param>
            /// <param name="sessionTimeout">会话超时时间。</param>
            /// <param name="chRoot">根节点。</param>
            public ZookeeperConfigInfo(string connectionString, TimeSpan sessionTimeout, string routePath = "/dotnet/serviceRoutes", string chRoot = null)
            {
                ChRoot = chRoot;
                ConnectionString = connectionString;
                RoutePath = routePath;
                SessionTimeout = sessionTimeout;
            }

            /// <summary>
            /// 连接字符串。
            /// </summary>
            public string ConnectionString { get; set; }

            /// <summary>
            /// 路由配置路径。
            /// </summary>
            public string RoutePath { get; set; }

            /// <summary>
            /// 会话超时时间。
            /// </summary>
            public TimeSpan SessionTimeout { get; set; }

            /// <summary>
            /// 根节点。
            /// </summary>
            public string ChRoot { get; set; }
        }

        protected class ConfigModel
        {
            public IpAddressDescriptor[] Routes { get; set; }
        }

        protected class IpAddressDescriptor
        {
            public List<IpAddressModel> Address { get; set; }
            public ServiceDescriptor ServiceDescriptor { get; set; }
        }

        #endregion Help Class

        #region Implementation of IDisposable

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            _connectionWait.Dispose();
            _zooKeeper.Dispose();
        }

        #endregion Implementation of IDisposable
    }
}