﻿using System;
using System.Diagnostics;
using System.ServiceProcess;
using HMQService.Common;
using HMQService.Server;
using HMQService.Decode;
using System.Net;
using System.Configuration.Install;
using System.Reflection;

namespace HMQService
{
    class HMQService : ServiceBase
    {
        private System.ComponentModel.Container components = null;
        private HMQManager hmqManager = null;

        /// <summary>
        /// Public Constructor for WindowsService.
        /// - Put all of your Initialization code here.
        /// </summary>
        public HMQService()
        {
            //初始化 log4net 配置信息
            log4net.Config.XmlConfigurator.Configure();

            //设置服务运行路径
            System.IO.Directory.SetCurrentDirectory(System.AppDomain.CurrentDomain.BaseDirectory);

            Log.GetLogger().InfoFormat("HMQService Constructor");

            InitializeComponent();
        }

        /// <summary>
        /// The Main Thread: This is where your Service is Run.
        /// </summary>
        static void Main(string[] args)
        {
            //if (System.Environment.UserInteractive)
            //{
            //    string parameter = string.Concat(args);
            //    switch (parameter)
            //    {
            //        case "--install":
            //        case "-install":
            //        case "/install":
            //            {
            //                Log.GetLogger().InfoFormat("install service");
            //                ManagedInstallerClass.InstallHelper(new string[] { Assembly.GetExecutingAssembly().Location });
            //            }
            //            break;
            //        case "--uninstall":
            //        case "-uninstall":
            //        case "/uninstall":
            //            {
            //                Log.GetLogger().InfoFormat("uninstall service");
            //                ManagedInstallerClass.InstallHelper(new string[] { "/u", Assembly.GetExecutingAssembly().Location });
            //            }
            //            break;
            //    }
            //}
            //else
            {
                Log.GetLogger().InfoFormat("HMQService Main");

                System.ServiceProcess.ServiceBase[] ServicesToRun;

                // More than one user Service may run within the same process. To add
                // another service to this process, change the following line to
                // create a second service object. For example,
                //
                //   ServicesToRun = new System.ServiceProcess.ServiceBase[] {new TCPService(), new MySecondUserService()};
                //
                ServicesToRun = new System.ServiceProcess.ServiceBase[] { new HMQService() };
                System.ServiceProcess.ServiceBase.Run(ServicesToRun);
            }

        }

        /// <summary> 
		/// Required method for Designer support - do not modify 
		/// the contents of this method with the code editor.
		/// </summary>
		private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            this.ServiceName = "HMQ Service";
            this.EventLog.Log = "Application";

            // These Flags set whether or not to handle that specific
            //  type of event. Set to true if you need it, false otherwise.
            this.CanHandlePowerEvent = true;
            this.CanHandleSessionChangeEvent = true;
            this.CanPauseAndContinue = true;
            this.CanShutdown = true;
            this.CanStop = true;
        }

        /// <summary>
        /// Dispose of objects that need it here.
        /// </summary>
        /// <param name="disposing">Whether
        ///    or not disposing is going on.</param>
        protected override void Dispose(bool disposing)
        {
            Log.GetLogger().InfoFormat("HMQService Dispose");

            if (disposing)
            {
                if (components != null)
                {
                    components.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// OnStart(): Put startup code here
        ///  - Start threads, get inital data, etc.
        /// </summary>
        /// <param name="args"></param>
        protected override void OnStart(string[] args)
        {
            Log.GetLogger().InfoFormat("HMQService OnStart");

            StartService();

            base.OnStart(args);
        }

        /// <summary>
        /// OnStop(): Put your stop code here
        /// - Stop threads, set final data, etc.
        /// </summary>
        protected override void OnStop()
        {
            Log.GetLogger().InfoFormat("HMQService OnStop");

            StopService();

            base.OnStop();
        }

        /// <summary>
        /// OnPause: Put your pause code here
        /// - Pause working threads, etc.
        /// </summary>
        protected override void OnPause()
        {
            Log.GetLogger().InfoFormat("HMQService OnPause");

            base.OnPause();
        }

        /// <summary>
        /// OnContinue(): Put your continue code here
        /// - Un-pause working threads, etc.
        /// </summary>
        protected override void OnContinue()
        {
            Log.GetLogger().InfoFormat("HMQService OnContinue");

            base.OnContinue();
        }

        /// <summary>
        /// OnShutdown(): Called when the System is shutting down
        /// - Put code here when you need special handling
        ///   of code that deals with a system shutdown, such
        ///   as saving special data before shutdown.
        /// </summary>
        protected override void OnShutdown()
        {
            Log.GetLogger().InfoFormat("HMQService OnShutdown");

            base.OnShutdown();
        }

        /// <summary>
        /// OnCustomCommand(): If you need to send a command to your
        ///   service without the need for Remoting or Sockets, use
        ///   this method to do custom methods.
        /// </summary>
        /// <param name="command">Arbitrary Integer between 128 & 256</param>
        protected override void OnCustomCommand(int command)
        {
            //  A custom command can be sent to a service by using this method:
            //#  int command = 128; //Some Arbitrary number between 128 & 256
            //#  ServiceController sc = new ServiceController("NameOfService");
            //#  sc.ExecuteCommand(command);

            Log.GetLogger().InfoFormat("HMQService OnCustomCommand");

            base.OnCustomCommand(command);
        }

        /// <summary>
        /// OnPowerEvent(): Useful for detecting power status changes,
        ///   such as going into Suspend mode or Low Battery for laptops.
        /// </summary>
        /// <param name="powerStatus">The Power Broadcast Status
        /// (BatteryLow, Suspend, etc.)</param>
        protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
        {
            Log.GetLogger().InfoFormat("HMQService OnPowerEvent");

            return base.OnPowerEvent(powerStatus);
        }

        /// <summary>
        /// OnSessionChange(): To handle a change event
        ///   from a Terminal Server session.
        ///   Useful if you need to determine
        ///   when a user logs in remotely or logs off,
        ///   or when someone logs into the console.
        /// </summary>
        /// <param name="changeDescription">The Session Change
        /// Event that occured.</param>
        protected override void OnSessionChange(
                  SessionChangeDescription changeDescription)
        {
            Log.GetLogger().InfoFormat("HMQService OnSessionChange");

            base.OnSessionChange(changeDescription);
        }

        private void StartService()
        {
            hmqManager = new HMQManager();
            hmqManager.StartWork();
        }

        private void StopService()
        {
            hmqManager.StopWork();
            hmqManager = null;
        }
    }
}