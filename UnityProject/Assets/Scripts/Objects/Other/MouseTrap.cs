﻿using System;
using Systems.Mob;
using Systems.MobAIs;
using UnityEngine.EventSystems;
using UnityEngine;
using HealthV2;


namespace Objects.Other
{
	public class MouseTrap : FloorHazard, IInteractable<HandActivate>, ICheckedInteractable<HandApply>
	{

		[SerializeField] private bool isArmed;
		[SerializeField] protected bool ignoresHandwear;
		[SerializeField] private ItemTrait trapTrait;
		[SerializeField] private SpriteHandler trapPreview;
		[SerializeField] private ItemStorage trapContent;

		private BodyPartType[] handTypes = {BodyPartType.LeftArm, BodyPartType.RightArm};
		private bool trapInSnare;
		public bool IsArmed => isArmed;

		public void Awake()
		{
			if (trapPreview == null)
			{
				Logger.LogError($"{gameObject} spawned with a null trapPreview. We can't get it on awake due to the existence of two SpriteHandlers!");
			}
		}

		private bool ArmTrap(GameObject Performer)
		{
			isArmed = !isArmed;
			Chat.AddExamineMsgFromServer(Performer,
				isArmed ? "You arm the " + gameObject.ExpensiveName() : "You disarm the " + gameObject.ExpensiveName());
			return isArmed;
		}

		/// <summary>
		/// for triggering traps when inside storage containers
		/// </summary>
		/// <param name="health"></param>
		private void HurtHand(LivingHealthMasterBase health)
		{
			foreach (var hand in health.playerScript.DynamicItemStorage.GetNamedItemSlots(NamedSlot.hands))
			{
				if(ignoresHandwear == false && hand.IsEmpty == false) continue;
				ApplyDamageToPartyType(health, handTypes.PickRandom());
			}
			Chat.AddActionMsgToChat(gameObject, $"You are surprised with a {gameObject.ExpensiveName()} biting your hand!",
				$"{health.playerScript.visibleName} screams in pain and surprise as {gameObject.ExpensiveName()} " +
				$"bites {health.playerScript.characterSettings.TheirPronoun(health.playerScript)} hand!");
			PlayStepAudio();
		}

		/// <summary>
		/// Things to trigger for attached items if found + hurting peoples hands.
		/// </summary>
		/// <param name="health"></param>
		public void TriggerTrap(LivingHealthMasterBase health = null)
		{
			if(health != null) HurtHand(health);
			var slot = trapContent.GetTopOccupiedIndexedSlot();
			if(slot == null) return;
			if (slot.ItemObject.TryGetComponent<ITrapComponent>(out var component))
			{
				if(Inventory.ServerDrop(slot)) trapInSnare = false;
				component.TriggerTrap();
			}
			UpdateTrapVisual();
			isArmed = false;
		}

		private bool HasTrapTrait(GameObject item)
		{
			if (item.Item() != null)
			{
				if (item.Item().HasTrait(trapTrait)) return true;
			}
			return false;
		}

		private void UpdateTrapVisual()
		{
			if (trapContent.GetNextFreeIndexedSlot() != null)
			{
				trapPreview.Empty();
				return;
			}
			//We assume that there will be only one item on each mouse trap (for now)
			var slot = trapContent.GetTopOccupiedIndexedSlot();
			var sprite = slot.Item.gameObject.GetComponentInChildren<SpriteHandler>();
			if (sprite.GetCurrentSpriteSO() == null)
			{
				trapPreview.SetSprite(sprite.CurrentSprite);
				return;
			}
			trapPreview.SetSpriteSO(sprite.GetCurrentSpriteSO());
		}

		public override void OnStep(GameObject eventData)
		{
			if (IsArmed == false) return;
			if(trapInSnare) TriggerTrap();
			//a mouse trap must kill mice, duh
			//TODO : IEnterable is designed for players only so mice can't trigger this :(
			if (eventData.TryGetComponent<MouseAI>(out var mouse))
			{
				mouse.health.Death();
				return;
			}
			base.OnStep(eventData);
			isArmed = false;
		}

		public override bool WillStep(GameObject eventData)
		{
			if (eventData.gameObject.TryGetComponent<LivingHealthMasterBase>(out var _)) return true;
			if (eventData.gameObject.TryGetComponent<MouseAI>(out var _mouse)) return true;
			return false;
		}

		public bool WillInteract(HandApply interaction, NetworkSide side)
		{
			if (!DefaultWillInteract.Default(interaction, side)) return false;
			if (interaction.UsedObject == null) return false;
			return true;
		}

		public void ServerPerformInteraction(HandActivate interaction)
		{
			if (ArmTrap(interaction.Performer) == false)
			{
				if(trapContent.GetNextFreeIndexedSlot() == null) trapContent.ServerDropAll();
			}
		}

		public void ServerPerformInteraction(HandApply interaction)
		{
			if (HasTrapTrait(interaction.UsedObject))
			{
				if (trapContent.ServerTryTransferFrom(interaction.UsedObject))
				{
					trapInSnare = true;
					UpdateTrapVisual();
				}

				if (isArmed == false)
				{
					ArmTrap(interaction.Performer);
				}
			}
		}
	}
}