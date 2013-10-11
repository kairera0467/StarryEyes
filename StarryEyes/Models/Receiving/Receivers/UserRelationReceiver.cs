﻿using System;
using System.Linq;
using System.Reactive.Linq;
using StarryEyes.Albireo.Data;
using StarryEyes.Anomaly.TwitterApi.Rest;
using StarryEyes.Models.Accounting;
using StarryEyes.Models.Backstages.NotificationEvents;
using StarryEyes.Settings;

namespace StarryEyes.Models.Receiving.Receivers
{
    public class UserRelationReceiver : CyclicReceiverBase
    {
        private readonly TwitterAccount _account;

        public UserRelationReceiver(TwitterAccount account)
        {
            this._account = account;
        }

        protected override int IntervalSec
        {
            get { return Setting.UserRelationReceivePeriod.Value; }
        }

        protected override void DoReceive()
        {
            // get relation account
            var reldata = this._account.RelationData;
            var beforeFollowing = new AVLTree<long>(reldata.Following);
            var beforeFollowers = new AVLTree<long>(reldata.Followers);
            var beforeBlockings = new AVLTree<long>(reldata.Blockings);
            // get followings / followers
            Observable.Merge(
                this._account.RetrieveAllCursor((a, c) => a.GetFriendsIdsAsync(this._account.Id, c))
                    .Do(id => beforeFollowing.Remove(id))
                    .Do(id => reldata.SetFollowingAsync(id, true)),
                this._account.RetrieveAllCursor((a, c) => a.GetFollowersIdsAsync(this._account.Id, c))
                    .Do(id => beforeFollowers.Remove(id))
                    .Do(id => reldata.SetFollowerAsync(id, true)),
                this._account.RetrieveAllCursor((a, c) => a.GetBlockingsIdsAsync(c))
                    .Do(id => beforeBlockings.Remove(id))
                    .Do(id => reldata.SetBlockingAsync(id, true)))
                      .Subscribe(_ => { },
                                 ex =>
                                 {
                                     BackstageModel.RegisterEvent(
                                         new OperationFailedEvent("relation receive error: " +
                                                                  this._account.UnreliableScreenName + " - " +
                                                                  ex.Message));
                                     System.Diagnostics.Debug.WriteLine(ex);
                                 },
                                 () =>
                                 {
                                     // cleanup remains
                                     beforeFollowing.ForEach(id => reldata.SetFollowingAsync(id, false));
                                     beforeFollowers.ForEach(id => reldata.SetFollowerAsync(id, false));
                                     beforeBlockings.ForEach(id => reldata.SetBlockingAsync(id, false));
                                 });
        }
    }
}