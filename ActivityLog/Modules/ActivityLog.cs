/*
 * Copyright (c) by Virtual Ability, Inc.  (www.VirtualAbility.org)
 * This software is licensed under the Original BSD License.
 * This code is donated to the OpenSim community by Virtual Ability
 * Developed by Dreamland Metaverse  (www.dreamlandmetaverse.com)
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 1. Redistributions of source code must retain the above copyright
 *    notice, this list of conditions and the following disclaimer.
 * 2. Redistributions in binary form must reproduce the above copyright
 *    notice, this list of conditions and the following disclaimer in the
 *    documentation and/or other materials provided with the distribution.
 * 3. All advertising materials mentioning features or use of this software
 *    must display the following acknowledgement:
 *    This product includes software developed by the <organization>.
 * 4. Neither the name of the <organization> nor the
 *    names of its contributors may be used to endorse or promote products
 *    derived from this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY <COPYRIGHT HOLDER> ''AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Mono.Addins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using System.Timers;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

[assembly: Addin("ActivityLog", "0.1")]
[assembly: AddinDependency("OpenSim", "0.5")]

namespace OpenSim.Region.Framework.Interfaces
{
    /// <summary>
    /// Activity Log module.
    /// </summary>
    /// <remarks>
    /// This module implements activity log functionality for OpenSim.
    /// </remarks>

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ActivityLog")]

    public class ActivityLogModule : ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled = false;

        private bool m_logChatsEnabled = true;
        private bool m_logPresenceEnabled = true;
        private bool m_logAllChannels = false;
        private string m_messageOnEntry = "Notice: All chat in this region is recorded and logged by the region owner.";
        private string m_logDirectory = "./ActivityLogs";
        private int m_keepForDays = 31;
        private int m_timerInterval = 12 * 60 * 60 * 1000; // 12 hours

        private IConfigSource m_config;
        private bool m_first_scene = true;
        private readonly List<Scene> m_scenes = new List<Scene>();
        private readonly Dictionary<UUID, UUID> m_lastRegionID = new Dictionary<UUID, UUID>();
        private readonly Timer m_timer = new Timer();
        
        #region ISharedRegionModule Members

        public string Name
        {
            get { return "ActivityLog"; }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            m_log.Info("[ActivityLog]: Copyright (c) by Virtual Ability, Inc.  (www.VirtualAbility.org)");
            m_log.Info("[ActivityLog]: This software is licensed under the Original BSD License.");
            m_log.Info("[ActivityLog]: This code is donated to the OpenSim community by Virtual Ability");
            m_log.Info("[ActivityLog]: Developed by Dreamland Metaverse  (www.dreamlandmetaverse.com)");
            
            m_config = source;
            IConfig config = m_config.Configs["Activity Log"];

            if (null == config)
            {
                m_log.Warn("[ActivityLog] No configuration specified; disabled by default");
                return;
            }
            else
            {
                // Found configuration
                if (!config.GetBoolean("Enabled", false))
                {
                    m_log.Warn("[ActivityLog] Module disabled");
                    return;
                }
                
                // Module is enabled
                m_enabled = true;

                // Get configuration parameters
                m_logChatsEnabled = config.GetBoolean("LogChatsEnabled", m_logChatsEnabled);
                m_logPresenceEnabled = config.GetBoolean("LogPresenceEnabled", m_logPresenceEnabled);
                m_logAllChannels = config.GetBoolean("LogAllChannels", m_logAllChannels);

                char[] charsToTrim = {'/'};
                m_messageOnEntry = (config.GetString("MessageOnEntry", m_messageOnEntry)).TrimEnd(charsToTrim);

                m_logDirectory = config.GetString("LogDirectory", m_logDirectory);
                m_keepForDays = config.GetInt("KeepForDays", m_keepForDays);
                m_timerInterval = config.GetInt("TimerInterval", 12) * 60 * 60 * 1000; // hours
                
                // Start timer
                if (m_keepForDays > 0 && m_timerInterval > 0)
                {
                    m_timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
                    m_timer.Interval = m_timerInterval;
                    m_timer.Enabled = true;
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            // m_log.Debug("[ActivityLog] Closing region");

            if (!m_enabled) return;

        }

        public void AddRegion(Scene scene)
        {
            // m_log.DebugFormat("[ActivityLog]: Region {0} added", scene.RegionInfo.RegionID);

            if (!m_enabled) return;

            // Add scene         
            lock (m_scenes) m_scenes.Add(scene);

            // Add event handlers
            scene.EventManager.OnNewClient += NewClient;
            scene.EventManager.OnMakeRootAgent += MakeRootAgent;
            scene.EventManager.OnMakeChildAgent += MakeChildAgent;

            if (m_logChatsEnabled)
                scene.EventManager.OnChatFromClient += ChatFromClient;

            // First scene added
            if (m_first_scene)
            {
                // m_log.Debug("[ActivityLog] Adding first region");
                
                m_first_scene = false;

                // Register console commands
                scene.AddCommand(this, "activity users", "activity users",
                    "Shows activity users", ActivityUsers);
                    
                scene.AddCommand(this, "activity files", "activity files",
                    "Shows activity files", ActivityFiles);
                    
                scene.AddCommand(this, "activity delete", "activity delete",
                    "Delete old activity log files", ActivityDelete);
            }
        }

        public void RegionLoaded(Scene scene)
        {
            // m_log.DebugFormat("[ActivityLog]: Region {0} loaded", scene.RegionInfo.RegionID);
        }

        public void RemoveRegion(Scene scene)
        {
            // m_log.DebugFormat("[ActivityLog]: Region {0} removed", scene.RegionInfo.RegionID);

            if (!m_enabled) return;

            // Remove scene
            lock (m_scenes) m_scenes.Remove(scene);

            // Unregister event handlers
            scene.EventManager.OnNewClient -= NewClient;
            scene.EventManager.OnMakeRootAgent -= MakeRootAgent;
            scene.EventManager.OnMakeChildAgent -= MakeChildAgent;
            
            if (m_logChatsEnabled)
                scene.EventManager.OnChatFromClient -= ChatFromClient;
        }

        // REGION MESSAGE HANDLERS

        private void NewClient(IClientAPI client)
        {
            // m_log.DebugFormat("[ActivityLog]: NewClient {0}", client.AgentId);

            // Register client logout event
            client.OnLogout += ClientLogout;
        }

        private void MakeRootAgent(ScenePresence avatar)
        {
            // m_log.DebugFormat("[ActivityLog]: MakeRootAgent {0}", avatar.ControllingClient.AgentId);
            
            // Show warning message
            if (m_logChatsEnabled && m_messageOnEntry != "") ShowEntryMessage(avatar.ControllingClient);
            
            UUID agentID = avatar.ControllingClient.AgentId;
            UUID regionID = avatar.Scene.RegionInfo.RegionID;
            
            // Store last visited region; used by client logout event handler
            m_lastRegionID[agentID] = regionID;
            
            if (m_logPresenceEnabled) EnterRegion(agentID, regionID);
        }

        private void MakeChildAgent(ScenePresence avatar)
        {
            // m_log.DebugFormat("[ActivityLog]: MakeChildAgent {0}", avatar.ControllingClient.AgentId);

            UUID agentID = avatar.ControllingClient.AgentId;
            UUID regionID = avatar.Scene.RegionInfo.RegionID;

            m_lastRegionID.Remove(agentID);
            
            if (m_logPresenceEnabled) LeaveRegion(agentID, regionID);
        }

        public void ClientLogout(IClientAPI client)
        {
            // m_log.DebugFormat("[ActivityLog]: ClientLogout {0}", client.AgentId);
            
            // Try to get last region visited
            UUID agentID = client.AgentId;
            UUID regionID = UUID.Zero;
            m_lastRegionID.TryGetValue(agentID, out regionID);
            m_lastRegionID.Remove(agentID);

            if (m_logPresenceEnabled) LogoutRegion(agentID, regionID);
        }

        #endregion

        #region ActivityLog Functionality

        // ACTIVITY LOG EVENTS

        private void EnterRegion(UUID agentID, UUID regionID)
        {
            // m_log.DebugFormat("[ActivityLog] Enter event {0} {1}", agentID, regionID);

            LogRecord(regionID, String.Format("ARRIVED \"{0}\" {1}", UserName(agentID), agentID));
        }

        private void LeaveRegion(UUID agentID, UUID regionID)
        {
            // m_log.DebugFormat("[ActivityLog] Leave event {0} {1}", agentID, regionID);

            LogRecord(regionID, String.Format("DEPARTED \"{0}\" {1}", UserName(agentID), agentID));
        }

        private void LogoutRegion(UUID agentID, UUID regionID)
        {
            // m_log.DebugFormat("[ActivityLog] Leave event {0} {1}", agentID, regionID);

            LogRecord(regionID, String.Format("LOGOUT \"{0}\" {1}", UserName(agentID), agentID));
        }
        
        public void ChatFromClient(Object sender, OSChatMessage chat)
        {
            // m_log.DebugFormat("[ActivityLog] Chat event {0} {1} {2} {3}",
            //     chat.Sender.AgentId, chat.Scene.RegionInfo.RegionID, chat.Channel, chat.Message);
            
            // ignore start/stop chatting notifications and chat sent by objects
            if (chat.SenderObject != null || chat.Message == "") return;
            
            // check if all channels should be logged
            if (!m_logAllChannels && chat.Channel != 0) return;
            
            UUID agentID = chat.Sender.AgentId;
            UUID regionID = chat.Scene.RegionInfo.RegionID;
            
            LogRecord(regionID, String.Format("CHAT \"{0}\" {1} Channel {2}: {3}",
                UserName(agentID), agentID, chat.Channel, chat.Message));
        }
        
        // OPTIONAL ENTRY MESSAGE

        private void ShowEntryMessage(IClientAPI client)
        {
            // m_log.Debug("[ActivityLog] ShowEntryMessage");
            
            GridInstantMessage msg = new GridInstantMessage();
            msg.imSessionID = UUID.Zero.Guid;
            msg.fromAgentID = UUID.Zero.Guid;
            msg.toAgentID = client.AgentId.Guid;
            msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
            msg.fromAgentName = "System";
            msg.message = m_messageOnEntry;
            msg.dialog = (byte)OpenMetaverse.InstantMessageDialog.ConsoleAndChatHistory;
            msg.fromGroup = false;
            msg.offline = (byte)0;
            msg.ParentEstateID = 0;
            msg.Position = Vector3.Zero;
            msg.RegionID = UUID.Zero.Guid;
            msg.binaryBucket = new byte[0];

            client.SendInstantMessage(msg);
        }
        
        // LOG FILE HANDLING
        
        private void LogRecord(UUID regionID, string logRecord)
        {
            m_log.DebugFormat("[ActivityLog] LogRecord {0}", logRecord);
            
            using (StreamWriter sw = File.AppendText(LogFileName(regionID))) 
            {
                sw.Write(DateTime.Today.ToString("yyyy-MM-dd HH:mm "));
                sw.WriteLine(logRecord);
            }
        }
        
        private string LogFileName(UUID regionID)
        {
            return String.Format("{0}/ActivityLog-{1}-{2}.log", m_logDirectory, regionID, DateTime.Today.ToString("yyyy-MM-dd"));
        }
        
        public void DeleteOldLogFiles(int numDays)
        {          
            m_log.InfoFormat("[ActivityLog] Deleting log files older than {0} days...", numDays);
                        
            string[] files = Directory.GetFiles(m_logDirectory);
            
            foreach (string file in files)
            {
                FileInfo fi = new FileInfo(file);
                if (fi.LastAccessTime < DateTime.Now.AddDays(-numDays))
                {
                    fi.Delete();
                    m_log.Info(file + " deleted");
                }
            }
        }
        
        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            // Delete old log files
            DeleteOldLogFiles(m_keepForDays);
        }

        // GENERAL UTILITY FUNCTIONS

        internal Scene findScene(UUID agentID)
        {
            if (m_scenes == null) return null;

            ScenePresence avatar = null;
            
            foreach (Scene sc in m_scenes)
            {
                if (sc.TryGetScenePresence(agentID, out avatar))
                {
                    if (!avatar.IsChildAgent)
                    {
                        return avatar.Scene;
                    }
                }
            }
            
            return null;
        }

        internal ScenePresence findAvatar(UUID agentID)
        {
            if (m_scenes == null) return null;

            ScenePresence avatar = null;
            
            foreach (Scene sc in m_scenes)
            {
                if (sc.TryGetScenePresence(agentID, out avatar))
                {
                    if (!avatar.IsChildAgent)
                    {
                        return avatar;
                    }
                }
            }
            
            return null;
        }
        
        internal string UserName(UUID agentID)
        {
            if (m_scenes == null) return null;
                    
            ScenePresence avatar = findAvatar(agentID);
            
            if (avatar != null) return avatar.Name;
            
            Scene scene = m_scenes[0];
            IUserAccountService userAccountService = scene.UserAccountService;
            UserAccount ua = userAccountService.GetUserAccount(UUID.Zero, agentID);

            if (ua == null) return "unknown";

            return ua.Name;
        }

        // CONSOLE COMMANDS

        public void ActivityUsers(string module, string[] args)
        {
            m_log.Info("[ActivityLog] Current Users:");
            
            foreach (KeyValuePair<UUID,UUID> kv in m_lastRegionID)
                m_log.InfoFormat("[ActivityLog] User {0} Region {1}", kv.Key, kv.Value);
        }
        
        public void ActivityFiles(string module, string[] args)
        {
            m_log.Info("[ActivityLog] Log Files:");
            
            string[] files = Directory.GetFiles(m_logDirectory);
            
            foreach (string file in files) m_log.Info(file);
        }
        
        public void ActivityDelete(string module, string[] args)
        {
            int numDays = m_keepForDays;
            
            // Optional parameter for the number of days; 0, 1, 2, ...
            if (args.Length > 2)
                if (!Int32.TryParse(args[2], out numDays)) return;

            // Delete old log files
            if (numDays >= 0) DeleteOldLogFiles(numDays);
        }

        #endregion
    }
}
