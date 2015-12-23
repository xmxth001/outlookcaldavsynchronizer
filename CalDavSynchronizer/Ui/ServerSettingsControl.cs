﻿// This file is Part of CalDavSynchronizer (http://outlookcaldavsynchronizer.sourceforge.net/)
// Copyright (c) 2015 Gerhard Zehetbauer
// Copyright (c) 2015 Alexander Nimmervoll
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CalDavSynchronizer.Contracts;
using CalDavSynchronizer.DataAccess;
using CalDavSynchronizer.Implementation;
using CalDavSynchronizer.Implementation.ComWrappers;
using CalDavSynchronizer.Scheduling;
using CalDavSynchronizer.Ui.ConnectionTests;
using CalDavSynchronizer.Utilities;
using log4net;
using Microsoft.Office.Interop.Outlook;
using Exception = System.Exception;

namespace CalDavSynchronizer.Ui
{
  public partial class ServerSettingsControl : UserControl, IServerSettingsControl
  {
    private static readonly ILog s_logger = LogManager.GetLogger (MethodInfo.GetCurrentMethod().DeclaringType);

    private ISettingsFaultFinder _settingsFaultFinder;
    private IServerSettingsControlDependencies _dependencies;
    private IServerAdapterControl _serverAdapterTypeControl;

    private void UpdatePasswordEnabled ()
    {
      _passwordTextBox.Enabled = _serverAdapterTypeControl.SelectedServerAdapterType != ServerAdapterType.GoogleOAuth;
    }

    public void Initialize (ISettingsFaultFinder settingsFaultFinder, IServerSettingsControlDependencies dependencies)
    {
      InitializeComponent();

      _settingsFaultFinder = settingsFaultFinder;
      _dependencies = dependencies;

      _testConnectionButton.Click += _testConnectionButton_Click;
      _serverAdapterTypeControl = _serverAdapterTypeControlImp;
      _serverAdapterTypeControl.SelectedServerAdapterTypeChanged += _serverAdapterTypeControl_SelectedServerAdapterTypeChanged;
    }

    void _serverAdapterTypeControl_SelectedServerAdapterTypeChanged (object sender, EventArgs e)
    {
      UpdatePasswordEnabled();
    }

    private async void _testConnectionButton_Click (object sender, EventArgs e)
    {
      await TestServerConnection();
    }

    private async Task TestServerConnection ()
    {
      _testConnectionButton.Enabled = false;
      try
      {
        StringBuilder errorMessageBuilder = new StringBuilder();
        if (!OptionTasks.ValidateCalendarUrl (_calenderUrlTextBox.Text, errorMessageBuilder, false))
        {
          MessageBox.Show (errorMessageBuilder.ToString(), "The CalDav/CardDav Url is invalid", MessageBoxButtons.OK, MessageBoxIcon.Error);
          return;
        }

        var enteredUri = new Uri (_calenderUrlTextBox.Text);
        var webDavClient = CreateWebDavClient();

        Uri autoDiscoveredUrl;
        ResourceType autoDiscoveredResourceType;

        if (ConnectionTester.RequiresAutoDiscovery (enteredUri))
        {
          var autodiscoveryResult = await OptionTasks.DoAutoDiscovery (enteredUri, webDavClient, true, true, _dependencies.OutlookFolderType);
          if (autodiscoveryResult.WasCancelled)
            return;
          if (autodiscoveryResult.RessourceUrl != null)
          {
            autoDiscoveredUrl = autodiscoveryResult.RessourceUrl;
            autoDiscoveredResourceType = autodiscoveryResult.ResourceType;
          }
          else
          {
            var autodiscoveryResult2 = await OptionTasks.DoAutoDiscovery (enteredUri.AbsolutePath.EndsWith ("/") ? enteredUri : new Uri (enteredUri.ToString () + "/"), webDavClient, false, false, _dependencies.OutlookFolderType);
            if (autodiscoveryResult2.WasCancelled)
              return;
            if (autodiscoveryResult2.RessourceUrl != null)
            {
              autoDiscoveredUrl = autodiscoveryResult2.RessourceUrl;
              autoDiscoveredResourceType = autodiscoveryResult2.ResourceType;
            }
            else
              return;
          }
        }
        else
        {
          var result = await ConnectionTester.TestConnection (enteredUri, webDavClient, ResourceType.None);
          if (result.ResourceType != ResourceType.None)
          {
            _settingsFaultFinder.FixSynchronizationMode (result);

            OptionTasks.DisplayTestReport (
                result,
                _dependencies.SelectedSynchronizationModeRequiresWriteableServerResource,
                _dependencies.SelectedSynchronizationModeDisplayName,
                _dependencies.OutlookFolderType);
            return;
          }
          else
          {
            var autodiscoveryResult = await OptionTasks.DoAutoDiscovery (enteredUri, webDavClient, false, false, _dependencies.OutlookFolderType);
            if (autodiscoveryResult.WasCancelled)
              return;
            if (autodiscoveryResult.RessourceUrl != null)
            {
              autoDiscoveredUrl = autodiscoveryResult.RessourceUrl;
              autoDiscoveredResourceType = autodiscoveryResult.ResourceType;
            }
            else
            {
              var autodiscoveryResult2 = await OptionTasks.DoAutoDiscovery (enteredUri, webDavClient, true, true, _dependencies.OutlookFolderType);
              if (autodiscoveryResult2.RessourceUrl != null)
              {
                autoDiscoveredUrl = autodiscoveryResult2.RessourceUrl;
                autoDiscoveredResourceType = autodiscoveryResult2.ResourceType;
              }
              else
                return;
            }
          }
        }

        _calenderUrlTextBox.Text = autoDiscoveredUrl.ToString();

        var finalResult = await ConnectionTester.TestConnection (autoDiscoveredUrl, webDavClient, autoDiscoveredResourceType);

        _settingsFaultFinder.FixSynchronizationMode (finalResult);

        OptionTasks.DisplayTestReport (
            finalResult,
            _dependencies.SelectedSynchronizationModeRequiresWriteableServerResource,
            _dependencies.SelectedSynchronizationModeDisplayName,
            _dependencies.OutlookFolderType);
        ;
      }
      catch (Exception x)
      {
        s_logger.Error ("Exception while testing the connection.", x);
        string message = null;
        for (Exception ex = x; ex != null; ex = ex.InnerException)
          message += ex.Message + Environment.NewLine;
        MessageBox.Show (message, OptionTasks.ConnectionTestCaption);
      }
      finally
      {
        _testConnectionButton.Enabled = true;
      }
    }

    private IWebDavClient CreateWebDavClient ()
    {
      return SynchronizerFactory.CreateWebDavClient (
          _userNameTextBox.Text,
          _passwordTextBox.Text,
          TimeSpan.Parse (ConfigurationManager.AppSettings["calDavConnectTimeout"]),
          _serverAdapterTypeControl.SelectedServerAdapterType,
          _dependencies.CloseConnectionAfterEachRequest,
          _dependencies.ProxyOptions);
    }

    public ICalDavDataAccess CreateCalDavDataAccess ()
    {
      return new CalDavDataAccess (new Uri (_calenderUrlTextBox.Text), CreateWebDavClient());
    }

    public void SetOptions (Options value)
    {
      _emailAddressTextBox.Text = value.EmailAddress;
      _calenderUrlTextBox.Text = value.CalenderUrl;
      _userNameTextBox.Text = value.UserName;
      _passwordTextBox.Text = value.Password;

      _serverAdapterTypeControl.SelectedServerAdapterType = value.ServerAdapterType;
    }

    public void FillOptions (Options optionsToFill)
    {
      optionsToFill.EmailAddress = _emailAddressTextBox.Text;
      optionsToFill.CalenderUrl = _calenderUrlTextBox.Text;
      optionsToFill.UserName = _userNameTextBox.Text;
      optionsToFill.Password = _passwordTextBox.Text;

      optionsToFill.ServerAdapterType = _serverAdapterTypeControl.SelectedServerAdapterType;
    }

    public string CalendarUrl
    {
      get { return _calenderUrlTextBox.Text; }
    }

    public string EmailAddress
    {
      get { return _emailAddressTextBox.Text; }
    }
  }
}