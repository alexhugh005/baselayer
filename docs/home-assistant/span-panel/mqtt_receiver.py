"""Receive-path compatibility for the pinned PanelBench 2.5.3 transport.

Upstream LoopBoundTransport.subscribe drops the SDK's callback parameter and
_AiomqttPublisher never consumes Client.messages. Preserve that SDK contract,
forwarding (topic, payload) unchanged. No property decoding or energy/relay
calculation is duplicated here; the SDK and emitter own those operations.
"""
import asyncio
import contextlib
import logging
import time

import aiomqtt
from paho.mqtt.client import topic_matches_sub
from panelbench.emitter_adapter import runtime


class ReceivingPublisher(runtime._AiomqttPublisher):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.receiver = None
        self.receive_task = None
        self.online = False
        self.generation = 0
        self.last_publish = 0.0
        self.subscriptions = set()

    async def connect(self):
        await super().connect()
        self.online = True
        self.generation += 1
        self.receive_task = asyncio.create_task(self._receive())

    async def _receive(self):
        while True:
            try:
                async for message in self._client.messages:
                    if self.receiver:
                        self.receiver(str(message.topic), bytes(message.payload))
            except aiomqtt.MqttError as exc:
                logging.warning('Panel MQTT disconnected; reconnecting: %s', exc)
            finally:
                self.online = False
            with contextlib.suppress(aiomqtt.MqttError):
                await super().disconnect()
            delay = 1
            while not self.online:
                await asyncio.sleep(delay)
                try:
                    await super().connect()
                    for topic in tuple(self.subscriptions):
                        await super().subscribe(topic)
                    self.online = True
                    self.generation += 1
                    logging.info('Panel MQTT reconnected; restoring device tree')
                except (aiomqtt.MqttError, OSError) as exc:
                    logging.warning('Panel MQTT retry failed: %s', exc)
                    with contextlib.suppress(aiomqtt.MqttError):
                        await super().disconnect()
                    delay = min(delay * 2, 10)

    def is_connected(self):
        return self.online

    async def publish(self, topic, payload, qos=0, retain=False):
        # Discard stale queued telemetry during an outage. The live emitter's
        # complete tree is republished after each connection generation.
        if not self.online:
            return
        await super().publish(topic, payload, qos, retain)
        self.last_publish = time.monotonic()

    async def subscribe(self, topic):
        self.subscriptions.add(topic)
        if self.online:
            await super().subscribe(topic)

    async def disconnect(self):
        self.online = False
        if self.receive_task:
            self.receive_task.cancel()
            with contextlib.suppress(asyncio.CancelledError, aiomqtt.MqttError):
                await self.receive_task
            self.receive_task = None
        await super().disconnect()


class ReceivingTransport(runtime.LoopBoundTransport):
    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.callbacks = {}
        kwargs['publish'].__self__.receiver = self.receive

    def subscribe(self, sub, param=None, qos=1):
        if callable(param):
            self.callbacks[sub] = param
        return super().subscribe(sub, param=param, qos=qos)

    def receive(self, topic, payload):
        for subscription, callback in tuple(self.callbacks.items()):
            if topic_matches_sub(subscription, topic):
                try:
                    callback(topic, payload)
                except Exception:
                    logging.exception('PanelBench SDK command callback failed for %s', topic)


def install():
    runtime._AiomqttPublisher = ReceivingPublisher
    runtime.LoopBoundTransport = ReceivingTransport
