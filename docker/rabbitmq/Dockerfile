# syntax=docker/dockerfile:1
#
# RabbitMQ with the delayed-message-exchange plugin.
#
# The notification consumer uses MassTransit's UseDelayedRedelivery, which requires a
# broker-level delayed exchange. RabbitMQ ships the plugin out of the box in management
# images only as an opt-in; the .ez is downloaded here and enabled offline so
# `docker compose up` gets the same retry behaviour the tests exercise.
#
# The plugin version tracks the broker minor version. Bump both together.

FROM rabbitmq:4.1-management-alpine

ARG PLUGIN_VERSION=4.1.0

RUN wget -q -O /opt/rabbitmq/plugins/rabbitmq_delayed_message_exchange-${PLUGIN_VERSION}.ez \
        https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v${PLUGIN_VERSION}/rabbitmq_delayed_message_exchange-${PLUGIN_VERSION}.ez \
    && rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange
