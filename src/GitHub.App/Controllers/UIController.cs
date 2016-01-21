﻿using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using GitHub.Authentication;
using GitHub.Exports;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Services;
using GitHub.UI;
using GitHub.ViewModels;
using NullGuard;
using ReactiveUI;
using Stateless;
using System.Collections.Specialized;
using System.Linq;

namespace GitHub.Controllers
{
    [Export(typeof(IUIController))]
    public class UIController : IUIController, IDisposable
    {
        enum Trigger { Cancel = 0, Auth = 1, Create = 2, Clone = 3, Publish = 4, Next, Finish }

        readonly IExportFactoryProvider factory;
        readonly IUIProvider uiProvider;
        readonly IRepositoryHosts hosts;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        readonly IConnectionManager connectionManager;
        readonly Lazy<ITwoFactorChallengeHandler> lazyTwoFactorChallengeHandler;

        readonly CompositeDisposable disposables = new CompositeDisposable();
        readonly StateMachine<UIViewType, Trigger> machine;
        Subject<UserControl> transition;
        Subject<bool> completion;
        UIControllerFlow currentFlow;
        NotifyCollectionChangedEventHandler connectionAdded;

        [ImportingConstructor]
        public UIController(IUIProvider uiProvider, IRepositoryHosts hosts, IExportFactoryProvider factory,
            IConnectionManager connectionManager, Lazy<ITwoFactorChallengeHandler> lazyTwoFactorChallengeHandler)
        {
            this.factory = factory;
            this.uiProvider = uiProvider;
            this.hosts = hosts;
            this.connectionManager = connectionManager;
            this.lazyTwoFactorChallengeHandler = lazyTwoFactorChallengeHandler;

#if DEBUG
            if (Application.Current != null && !Splat.ModeDetector.InUnitTestRunner())
            {
                var waitDispatcher = RxApp.MainThreadScheduler as WaitForDispatcherScheduler;
                if (waitDispatcher != null)
                {
                    Debug.Assert(DispatcherScheduler.Current.Dispatcher == Application.Current.Dispatcher,
                       "DispatcherScheduler is set correctly");
                }
                else
                {
                    Debug.Assert(((DispatcherScheduler)RxApp.MainThreadScheduler).Dispatcher == Application.Current.Dispatcher,
                        "The MainThreadScheduler is using the wrong dispatcher");
                }
            }
#endif
            machine = new StateMachine<UIViewType, Trigger>(UIViewType.None);

            machine.Configure(UIViewType.Login)
                .OnEntry(() =>
                {
                    RunView(UIViewType.Login);
                })
                .Permit(Trigger.Next, UIViewType.TwoFactor)
                // Added the following line to make it easy to login to both GitHub and GitHub Enterprise 
                // in DesignTimeStyleHelper in order to test Publish.
                .Permit(Trigger.Cancel, UIViewType.End)
                .PermitIf(Trigger.Finish, UIViewType.End, () => currentFlow == UIControllerFlow.Authentication)
                .PermitIf(Trigger.Finish, UIViewType.Create, () => currentFlow == UIControllerFlow.Create)
                .PermitIf(Trigger.Finish, UIViewType.Clone, () => currentFlow == UIControllerFlow.Clone)
                .PermitIf(Trigger.Finish, UIViewType.Publish, () => currentFlow == UIControllerFlow.Publish);

            machine.Configure(UIViewType.TwoFactor)
                .SubstateOf(UIViewType.Login)
                .OnEntry(() =>
                {
                    RunView(UIViewType.TwoFactor);
                })
                .Permit(Trigger.Cancel, UIViewType.End)
                .PermitIf(Trigger.Next, UIViewType.End, () => currentFlow == UIControllerFlow.Authentication)
                .PermitIf(Trigger.Next, UIViewType.Create, () => currentFlow == UIControllerFlow.Create)
                .PermitIf(Trigger.Next, UIViewType.Clone, () => currentFlow == UIControllerFlow.Clone)
                .PermitIf(Trigger.Next, UIViewType.Publish, () => currentFlow == UIControllerFlow.Publish);

            machine.Configure(UIViewType.Create)
                .OnEntry(() =>
                {
                    RunView(UIViewType.Create);
                })
                .Permit(Trigger.Cancel, UIViewType.End)
                .Permit(Trigger.Next, UIViewType.End);

            machine.Configure(UIViewType.Clone)
                .OnEntry(() =>
                {
                    RunView(UIViewType.Clone);
                })
                .Permit(Trigger.Cancel, UIViewType.End)
                .Permit(Trigger.Next, UIViewType.End);

            machine.Configure(UIViewType.Publish)
                .OnEntry(() =>
                {
                    RunView(UIViewType.Publish);
                })
                .Permit(Trigger.Cancel, UIViewType.End)
                .Permit(Trigger.Next, UIViewType.End);

            machine.Configure(UIViewType.End)
                .OnEntryFrom(Trigger.Cancel, () => End(false))
                .OnEntryFrom(Trigger.Next, () => End(true))
                .OnEntryFrom(Trigger.Finish, () => End(true))
                .Permit(Trigger.Next, UIViewType.Finished);

            machine.Configure(UIViewType.Finished);
        }

        public IObservable<UserControl> SelectFlow(UIControllerFlow choice)
        {
            currentFlow = choice;

            transition = new Subject<UserControl>();
            transition.Subscribe(_ => { }, _ => Fire(Trigger.Next));
        
            return transition;
        }

        /// <summary>
        /// Allows listening to the completion state of the ui flow - whether
        /// it was completed because it was cancelled or whether it succeeded.
        /// </summary>
        /// <returns>true for success, false for cancel</returns>
        public IObservable<bool> ListenToCompletionState()
        {
            if (completion == null)
                completion = new Subject<bool>();
            return completion;
        }

        void End(bool success)
        {
            uiProvider.RemoveService(typeof(IConnection));
            completion?.OnNext(success);
            completion?.OnCompleted();
            transition.OnCompleted();
        }

        void RunView(UIViewType viewType)
        {
            var view = CreateViewAndViewModel(viewType);
            transition.OnNext(view as UserControl);
            SetupView(viewType, view);
        }

        void SetupView(UIViewType viewType, IView view)
        {
            if (viewType == UIViewType.Login)
            {
                // we're setting up the login dialog, we need to setup the 2fa as
                // well to continue the flow if it's needed, since the
                // authenticationresult callback won't happen until
                // everything is done
                var dvm = factory.GetViewModel(UIViewType.TwoFactor);
                disposables.Add(dvm);
                var twofa = dvm.Value;
                disposables.Add(twofa.WhenAny(x => x.IsShowing, x => x.Value)
                    .Where(x => x)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => Fire(Trigger.Next)));

                disposables.Add(view.Done
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => Fire(Trigger.Finish)));
            }
            else if (viewType != UIViewType.TwoFactor)
            {
                disposables.Add(view.Done
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => Fire(Trigger.Next)));
            }
            disposables.Add(view.Cancel.Subscribe(_ => Stop()));
        }

        IView CreateViewAndViewModel(UIViewType viewType)
        {
            IViewModel viewModel;
            if (viewType == UIViewType.TwoFactor)
            {
                viewModel = lazyTwoFactorChallengeHandler.Value.CurrentViewModel;
            }
            else
            {
                var dvm = factory.GetViewModel(viewType);
                disposables.Add(dvm);
                viewModel = dvm.Value;
            }
            
            var dv = factory.GetView(viewType);
            disposables.Add(dv);
            var view = dv.Value;

            view.ViewModel = viewModel;

            return view;
        }

        void Fire(Trigger next)
        {
            Debug.WriteLine("Firing {0} ({1})", next, GetHashCode());
            machine.Fire(next);
        }

        public void Start([AllowNull] IConnection connection)
        {
            if (connection != null)
            {
                if (currentFlow != UIControllerFlow.Authentication)
                    uiProvider.AddService(connection);
                else // sanity check: it makes zero sense to pass a connection in when calling the auth flow
                    Debug.Assert(false, "Calling the auth flow with a connection makes no sense!");

                connection.Login()
                    .Select(c => hosts.LookupHost(connection.HostAddress))
                    .Do(host =>
                    {
                        machine.Configure(UIViewType.None)
                            .Permit(Trigger.Auth, UIViewType.Login)
                            .PermitIf(Trigger.Create, UIViewType.Create, () => host.IsLoggedIn)
                            .PermitIf(Trigger.Create, UIViewType.Login, () => !host.IsLoggedIn)
                            .PermitIf(Trigger.Clone, UIViewType.Clone, () => host.IsLoggedIn)
                            .PermitIf(Trigger.Clone, UIViewType.Login, () => !host.IsLoggedIn)
                            .PermitIf(Trigger.Publish, UIViewType.Publish, () => host.IsLoggedIn)
                            .PermitIf(Trigger.Publish, UIViewType.Login, () => !host.IsLoggedIn);
                    })
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => { }, () =>
                    {
                        Debug.WriteLine("Start ({0})", GetHashCode());
                        Fire((Trigger)(int)currentFlow);
                    });
            }
            else
            {
                connectionManager
                    .GetLoggedInConnections(hosts)
                    .FirstOrDefaultAsync()
                    .Select(c =>
                    {
                        bool loggedin = c != null;
                        if (currentFlow != UIControllerFlow.Authentication)
                        {
                            if (loggedin) // register the first available connection so the viewmodel can use it
                                uiProvider.AddService(c);
                            else
                            {
                                // a connection will be added to the list when auth is done, register it so the next
                                // viewmodel can use it
                                connectionAdded = (s, e) =>
                                {
                                    if (e.Action == NotifyCollectionChangedAction.Add)
                                        uiProvider.AddService(typeof(IConnection), e.NewItems[0]);
                                };
                                connectionManager.Connections.CollectionChanged += connectionAdded;
                            }
                        }

                        machine.Configure(UIViewType.None)
                            .Permit(Trigger.Auth, UIViewType.Login)
                            .PermitIf(Trigger.Create, UIViewType.Create, () => loggedin)
                            .PermitIf(Trigger.Create, UIViewType.Login, () => !loggedin)
                            .PermitIf(Trigger.Clone, UIViewType.Clone, () => loggedin)
                            .PermitIf(Trigger.Clone, UIViewType.Login, () => !loggedin)
                            .PermitIf(Trigger.Publish, UIViewType.Publish, () => loggedin)
                            .PermitIf(Trigger.Publish, UIViewType.Login, () => !loggedin);

                        return loggedin;
                    })
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => { }, () =>
                    {
                        Debug.WriteLine("Start ({0})", GetHashCode());
                        Fire((Trigger)(int)currentFlow);
                    });
            }
        }

        public void Stop()
        {
            Debug.WriteLine("Stop ({0})", GetHashCode());
            Fire(machine.IsInState(UIViewType.End) ? Trigger.Next : Trigger.Cancel);
        }

        bool disposed; // To detect redundant calls
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (disposed) return;
                disposed = true;

                if (connectionAdded != null)
                    connectionManager.Connections.CollectionChanged -= connectionAdded;
                connectionAdded = null;

                var tr = transition;
                var cmp = completion;
                transition = null;
                completion = null;
                disposables.Dispose();
                tr?.Dispose();
                cmp?.Dispose();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool IsStopped => machine.IsInState(UIViewType.Finished);
    }
}
